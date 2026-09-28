using HockeyPlanner.Backend.Application.Abstractions.Services;
using HockeyPlanner.Backend.Core.Enums;
using HockeyPlanner.Backend.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace HockeyPlanner.Backend.WebAPI.Services;

// Delivery is separate from durable enqueue in the business transaction.
public sealed class NotificationService(AppDbContext context, NotificationOutbox outbox) : INotificationService
{
    public Task NotifyUserAsync(Guid userId, NotificationType type, NotificationCategory category,
        string title, string body, string? url = null, CancellationToken cancellationToken = default) =>
        NotifyUsersAsync([userId], type, category, title, body, url, cancellationToken);

    public Task NotifyUsersAsync(IReadOnlyCollection<Guid> userIds, NotificationType type, NotificationCategory category,
        string title, string body, string? url = null, CancellationToken cancellationToken = default) =>
        NotifyUsersOnceAsync(Guid.NewGuid().ToString("N"), userIds, type, category, title, body, url, cancellationToken);

    public Task NotifyUsersOnceAsync(string logicalKey, IReadOnlyCollection<Guid> userIds, NotificationType type,
        NotificationCategory category, string title, string body, string? url, CancellationToken cancellationToken) =>
        outbox.EnqueueAsync(userIds, logicalKey, type, category, title, body, url, cancellationToken);

    public async Task NotifyTeamAsync(Guid teamId, NotificationType type, NotificationCategory category,
        string title, string body, string? url = null, CancellationToken cancellationToken = default)
    {
        var ids = await context.TeamMemberships.AsNoTracking().Where(value => value.TeamId == teamId)
            .Select(value => value.UserId).Distinct().ToArrayAsync(cancellationToken);
        await NotifyUsersAsync(ids, type, category, title, body, url, cancellationToken);
    }
}
