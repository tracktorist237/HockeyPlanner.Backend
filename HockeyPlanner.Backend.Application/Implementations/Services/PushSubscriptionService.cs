using HockeyPlanner.Backend.Application.Abstractions.Services;
using HockeyPlanner.Backend.Core.Entities;
using HockeyPlanner.Backend.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace HockeyPlanner.Backend.Application.Implementations.Services;

internal sealed class PushSubscriptionService : IPushSubscriptionService
{
    private const string EndpointUniqueConstraint = "i_x_push_subscriptions_endpoint";

    private readonly AppDbContext _context;
    private readonly TimeProvider _timeProvider;

    public PushSubscriptionService(AppDbContext context, TimeProvider timeProvider)
    {
        _context = context;
        _timeProvider = timeProvider;
    }

    public async Task<PushSubscriptionResult> Subscribe(
        Guid actorUserId,
        PushSubscriptionInput input,
        CancellationToken cancellationToken)
    {
        var endpoint = input.Endpoint.Trim();
        var existing = await _context.PushSubscriptions
            .SingleOrDefaultAsync(subscription => subscription.Endpoint == endpoint, cancellationToken);
        var now = _timeProvider.GetUtcNow().UtcDateTime;

        if (existing is not null)
        {
            return await UpdateExisting(existing, actorUserId, input, now, cancellationToken);
        }

        var subscription = new PushSubscription
        {
            Endpoint = endpoint,
            P256dhKey = input.P256dh.Trim(),
            AuthKey = input.Auth.Trim(),
            UserId = actorUserId,
            UserAgent = NormalizeOptional(input.UserAgent),
            Platform = NormalizeOptional(input.Platform),
            DeviceName = NormalizeOptional(input.DeviceName),
            IsActive = true,
            LastSeenAt = now,
            RevokedAt = null,
            CreatedAt = now,
            UpdatedAt = now,
        };

        await _context.PushSubscriptions.AddAsync(subscription, cancellationToken);

        try
        {
            await _context.SaveChangesAsync(cancellationToken);
            return PushSubscriptionResult.Success;
        }
        catch (DbUpdateException exception) when (IsEndpointUniqueViolation(exception))
        {
            _context.Entry(subscription).State = EntityState.Detached;

            existing = await _context.PushSubscriptions
                .SingleAsync(value => value.Endpoint == endpoint, cancellationToken);

            return await UpdateExisting(existing, actorUserId, input, now, cancellationToken);
        }
    }

    public async Task Unsubscribe(
        Guid actorUserId,
        string endpoint,
        CancellationToken cancellationToken)
    {
        var normalizedEndpoint = endpoint.Trim();
        var existing = await _context.PushSubscriptions
            .SingleOrDefaultAsync(
                subscription =>
                    subscription.Endpoint == normalizedEndpoint &&
                    subscription.UserId == actorUserId,
                cancellationToken);

        if (existing is null || !existing.IsActive)
        {
            return;
        }

        var now = _timeProvider.GetUtcNow().UtcDateTime;
        existing.IsActive = false;
        existing.RevokedAt = now;
        existing.UpdatedAt = now;
        await _context.SaveChangesAsync(cancellationToken);
    }

    private async Task<PushSubscriptionResult> UpdateExisting(
        PushSubscription existing,
        Guid actorUserId,
        PushSubscriptionInput input,
        DateTime now,
        CancellationToken cancellationToken)
    {
        var p256dh = input.P256dh.Trim();
        var auth = input.Auth.Trim();
        var sameOwner = existing.UserId == actorUserId;
        var sameKeys = string.Equals(existing.P256dhKey, p256dh, StringComparison.Ordinal) &&
                       string.Equals(existing.AuthKey, auth, StringComparison.Ordinal);

        if (!sameOwner && !sameKeys)
        {
            return PushSubscriptionResult.Conflict;
        }

        existing.P256dhKey = p256dh;
        existing.AuthKey = auth;
        existing.UserId = actorUserId;
        existing.UserAgent = NormalizeOptional(input.UserAgent);
        existing.Platform = NormalizeOptional(input.Platform);
        existing.DeviceName = NormalizeOptional(input.DeviceName);
        existing.IsActive = true;
        existing.LastSeenAt = now;
        existing.RevokedAt = null;
        existing.UpdatedAt = now;

        await _context.SaveChangesAsync(cancellationToken);
        return PushSubscriptionResult.Success;
    }

    private static bool IsEndpointUniqueViolation(DbUpdateException exception) =>
        exception.InnerException is PostgresException
        {
            SqlState: PostgresErrorCodes.UniqueViolation,
            ConstraintName: EndpointUniqueConstraint,
        };

    private static string? NormalizeOptional(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
