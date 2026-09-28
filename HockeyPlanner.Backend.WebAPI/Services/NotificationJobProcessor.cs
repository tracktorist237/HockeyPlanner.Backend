using System.Security.Cryptography;
using HockeyPlanner.Backend.Core.Entities;
using HockeyPlanner.Backend.Core.Enums;
using HockeyPlanner.Backend.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace HockeyPlanner.Backend.WebAPI.Services;

public sealed class NotificationJobProcessor(
    AppDbContext db,
    IWebPushService push,
    TimeProvider clock,
    IOptions<NotificationWorkerOptions> options,
    ILogger<NotificationJobProcessor> logger,
    AuthEmailOutbox? emails = null)
{
    public async Task ProcessAsync(Guid jobId, CancellationToken cancellationToken)
    {
        // A session lock covers IO, but no database transaction does. A crashed
        // connection releases it; persisted claims are recoverable after timeout.
        var key = BitConverter.ToInt64(SHA256.HashData(jobId.ToByteArray()), 0);
        await db.Database.OpenConnectionAsync(cancellationToken);
        var locked = false;
        try
        {
            locked = await db.Database.SqlQuery<bool>($"SELECT pg_try_advisory_lock({key}) AS \"Value\"")
                .SingleAsync(cancellationToken);
            if (!locked) return;
            var settings = options.Value;
            var now = clock.GetUtcNow().UtcDateTime;
            var staleBefore = now.AddSeconds(-settings.ClaimTimeoutSeconds);
            var job = await db.NotificationJobs.Include(value => value.Notification)
                .SingleOrDefaultAsync(value => value.Id == jobId, cancellationToken);
            if (job is null || job.Status is NotificationJobStatus.Succeeded or NotificationJobStatus.Failed) return;
            if (job.Status == NotificationJobStatus.Pending && job.NextAttemptAt > now) return;
            if (job.Status == NotificationJobStatus.Processing && job.ClaimedAt > staleBefore) return;
            if (job.AttemptCount >= settings.MaxAttempts)
            {
                job.Status = NotificationJobStatus.Failed;
                job.LastErrorCode = "attempts_exhausted";
                job.CompletedAt = now;
                job.ProtectedPayload = null;
                job.ClaimId = null;
                await db.SaveChangesAsync(cancellationToken);
                logger.LogWarning("Notification job terminal failure: JobId {JobId}, Type {Type}, Attempt {Attempt}, ErrorCode {ErrorCode}",
                    job.Id, job.Kind, job.AttemptCount, job.LastErrorCode);
                return;
            }

            var recovered = job.Status == NotificationJobStatus.Processing;
            job.Status = NotificationJobStatus.Processing;
            job.AttemptCount++;
            job.ClaimId = Guid.NewGuid();
            job.ClaimedAt = now;
            job.UpdatedAt = now;
            await db.SaveChangesAsync(cancellationToken);
            logger.LogInformation("Notification job claimed: JobId {JobId}, Type {Type}, Attempt {Attempt}, Recovered {Recovered}",
                job.Id, job.Kind, job.AttemptCount, recovered);
            bool transient = false;
            string? error = null;
            try
            {
                if (job.Kind == NotificationJobKind.Push)
                    (transient, error) = await DeliverAsync(job.Notification!, cancellationToken);
                else
                {
                    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                    timeout.CancelAfter(TimeSpan.FromSeconds(settings.DeliveryTimeoutSeconds));
                    await (emails ?? throw new InvalidOperationException("Email adapter missing.")).DeliverAsync(job, timeout.Token);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // Leave a durable claim for recovery; never label shutdown an upstream failure.
                throw;
            }
            catch (Exception exception)
            {
                transient = IsTransientFailure(exception);
                error = transient ? "transport_failure" : "delivery_failure";
                if (job.Kind != NotificationJobKind.Push)
                    logger.LogWarning("Authentication email delivery {Outcome}: type={EmailKind}, user={UserId}, error={ErrorType}",
                        exception is TimeoutException or OperationCanceledException ? "timed out" : "failed",
                        job.Kind == NotificationJobKind.EmailConfirmation ? "email confirmation" : "password reset",
                        job.UserId, exception.GetType().Name);
            }

            now = clock.GetUtcNow().UtcDateTime;
            job.LastErrorCode = error;
            job.UpdatedAt = now;
            job.ClaimId = null;
            job.Status = error is null ? NotificationJobStatus.Succeeded
                : transient && job.AttemptCount < settings.MaxAttempts ? NotificationJobStatus.Pending
                : NotificationJobStatus.Failed;
            if (job.Status == NotificationJobStatus.Pending)
            {
                var delay = Math.Min(3600, settings.RetryDelaySeconds * Math.Pow(2, job.AttemptCount - 1));
                job.NextAttemptAt = now.AddSeconds(delay);
            }
            else
            {
                job.CompletedAt = now;
                job.ProtectedPayload = null;
            }
            await db.SaveChangesAsync(cancellationToken);
            if (job.Status == NotificationJobStatus.Failed)
                logger.LogWarning("Notification job terminal failure: JobId {JobId}, Type {Type}, Attempt {Attempt}, ErrorCode {ErrorCode}",
                    job.Id, job.Kind, job.AttemptCount, error);
            else if (job.Status == NotificationJobStatus.Pending)
                logger.LogInformation("Notification job retry scheduled: JobId {JobId}, Type {Type}, Attempt {Attempt}, NextAttemptAt {NextAttemptAt}, ErrorCode {ErrorCode}",
                    job.Id, job.Kind, job.AttemptCount, job.NextAttemptAt, error);
            logger.LogInformation("Notification job finished: JobId {JobId}, Type {Type}, Attempt {Attempt}, Outcome {Outcome}, ErrorCode {ErrorCode}",
                job.Id, job.Kind, job.AttemptCount, job.Status, error);
        }
        finally
        {
            if (locked)
            {
                // Release independently of request cancellation. Closing also resets the pooled connection.
                try { await db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_unlock({key})", CancellationToken.None); }
                finally { await db.Database.CloseConnectionAsync(); }
            }
            else await db.Database.CloseConnectionAsync();
        }
    }

    private static bool IsTransientFailure(Exception exception) => exception switch
    {
        HttpRequestException http => http.StatusCode is null || (int)http.StatusCode >= 500
            || http.StatusCode is System.Net.HttpStatusCode.RequestTimeout or System.Net.HttpStatusCode.TooManyRequests,
        MailKit.Net.Smtp.SmtpCommandException smtp => (int)smtp.StatusCode is >= 400 and < 500,
        IOException or TimeoutException or OperationCanceledException => true,
        _ => false
    };

    private async Task<(bool Transient, string? Error)> DeliverAsync(Notification notification, CancellationToken token)
    {
        var preferences = await db.NotificationPreferences.AsNoTracking()
            .SingleOrDefaultAsync(value => value.UserId == notification.UserId, token);
        var enabled = preferences is null || notification.Category switch
        {
            NotificationCategory.AttendanceRequired => preferences.AttendanceRequiredEnabled,
            NotificationCategory.RosterReady => preferences.RosterReadyEnabled,
            NotificationCategory.TeamNews => preferences.TeamNewsEnabled,
            NotificationCategory.Goalies => preferences.GoaliesEnabled,
            NotificationCategory.Birthdays => preferences.BirthdaysEnabled,
            NotificationCategory.AppUpdates => preferences.AppUpdatesEnabled,
            _ => true
        };
        if (!enabled)
        {
            await RecordSkippedAsync(notification, "preferences_disabled", token);
            return (false, null);
        }
        if (!push.IsConfigured) return (true, "push_not_configured");
        var subscriptions = await db.PushSubscriptions
            .Where(value => value.UserId == notification.UserId && value.IsActive).ToArrayAsync(token);
        if (subscriptions.Length == 0)
        {
            await RecordSkippedAsync(notification, "no_active_subscription", token);
            return (false, null);
        }
        var deliveries = await db.NotificationDeliveries
            .Where(value => value.NotificationId == notification.Id).ToListAsync(token);
        bool retry = false;
        string? error = null;
        foreach (var subscription in subscriptions)
        {
            var delivery = deliveries.FirstOrDefault(value => value.PushSubscriptionId == subscription.Id);
            if (delivery?.Status is NotificationDeliveryStatus.Sent or NotificationDeliveryStatus.EndpointInactive) continue;
            if (delivery?.Status == NotificationDeliveryStatus.Failed && delivery.Error == "provider_rejected")
            {
                error = "provider_rejected";
                continue;
            }
            delivery ??= new NotificationDelivery
            {
                NotificationId = notification.Id, UserId = notification.UserId,
                PushSubscriptionId = subscription.Id, CreatedAt = clock.GetUtcNow().UtcDateTime
            };
            if (db.Entry(delivery).State == EntityState.Detached) db.NotificationDeliveries.Add(delivery);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(TimeSpan.FromSeconds(options.Value.DeliveryTimeoutSeconds));
            WebPushSendResult result;
            try
            {
                result = await push.SendAsync(subscription,
                    new { title = notification.Title, body = notification.Body, url = notification.Url }, timeout.Token);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
            catch (Exception exception) when (exception is HttpRequestException or TimeoutException or OperationCanceledException)
            {
                result = new WebPushSendResult { IsTransient = true };
            }
            var now = clock.GetUtcNow().UtcDateTime;
            delivery.UpdatedAt = now;
            if (result.IsSuccess)
            {
                delivery.Status = NotificationDeliveryStatus.Sent;
                delivery.SentAt = now;
                delivery.Error = null;
                notification.DeliveredAt = now;
            }
            else if (result.ShouldRemoveSubscription)
            {
                subscription.IsActive = false;
                subscription.RevokedAt = now;
                subscription.UpdatedAt = now;
                delivery.Status = NotificationDeliveryStatus.EndpointInactive;
                delivery.Error = "endpoint_inactive";
            }
            else
            {
                delivery.Status = NotificationDeliveryStatus.Failed;
                delivery.Error = result.IsTransient ? "provider_transient" : "provider_rejected";
                error = delivery.Error;
                retry |= result.IsTransient;
            }
            // A successful endpoint is persisted before proceeding to the next one.
            await db.SaveChangesAsync(token);
        }
        return (retry, error);
    }

    private async Task RecordSkippedAsync(Notification notification, string code, CancellationToken token)
    {
        if (await db.NotificationDeliveries.AnyAsync(value => value.NotificationId == notification.Id
            && value.PushSubscriptionId == null, token)) return;
        db.NotificationDeliveries.Add(new NotificationDelivery { NotificationId = notification.Id,
            UserId = notification.UserId, Status = NotificationDeliveryStatus.Skipped, Error = code,
            CreatedAt = clock.GetUtcNow().UtcDateTime });
        await db.SaveChangesAsync(token);
    }
}
