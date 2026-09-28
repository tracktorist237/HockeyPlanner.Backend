using System.Security.Cryptography;
using System.Text;
using HockeyPlanner.Backend.Core.Entities;
using HockeyPlanner.Backend.Core.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace HockeyPlanner.Backend.Infrastructure.Data;

public sealed class NotificationOutbox(AppDbContext db, TimeProvider clock, ILogger<NotificationOutbox> logger)
{
    public async Task EnqueueAsync(IReadOnlyCollection<Guid> userIds, string logicalKey, NotificationType type,
        NotificationCategory category, string title, string body, string? url, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(logicalKey) || logicalKey.Length > 200)
            throw new ArgumentException("A bounded logical key is required.", nameof(logicalKey));
        var recipients = userIds.Where(value => value != Guid.Empty).Distinct().Order().ToArray();
        if (recipients.Length == 0) return;
        await using var ownedTransaction = db.Database.CurrentTransaction is null
            ? await db.Database.BeginTransactionAsync(cancellationToken) : null;
        // Serialize retries of this logical operation. The unique index is the
        // final invariant even for writers that don't use this enqueue path.
        var key = BitConverter.ToInt64(SHA256.HashData(Encoding.UTF8.GetBytes("notification:" + logicalKey)), 0);
        await db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock({key})", cancellationToken);
        var existing = await db.Notifications.AsNoTracking()
            .Where(value => value.LogicalKey == logicalKey && recipients.Contains(value.UserId))
            .Select(value => value.UserId).ToArrayAsync(cancellationToken);
        var now = clock.GetUtcNow().UtcDateTime;
        foreach (var userId in recipients.Except(existing))
        {
            var notification = new Notification
            {
                UserId = userId, LogicalKey = logicalKey, Type = type, Category = category,
                Title = title.Trim(), Body = body.Trim(), Url = string.IsNullOrWhiteSpace(url) ? null : url.Trim(),
                CreatedAt = now, UpdatedAt = now
            };
            var job = new NotificationJob { Notification = notification, NotificationId = notification.Id,
                NextAttemptAt = now, CreatedAt = now, UpdatedAt = now };
            db.NotificationJobs.Add(job);
            logger.LogInformation("Notification job enqueued in current transaction: JobId {JobId}, Type {Type}", job.Id, type);
        }
        await db.SaveChangesAsync(cancellationToken);
        if (ownedTransaction is not null) await ownedTransaction.CommitAsync(cancellationToken);
    }
}
