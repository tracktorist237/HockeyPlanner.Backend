using HockeyPlanner.Backend.Core.Enums;
using HockeyPlanner.Backend.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace HockeyPlanner.Backend.WebAPI.Services;

public sealed record NotificationJobFailure(Guid JobId, NotificationJobKind Kind, int AttemptCount,
    string? ErrorCode, DateTime? CompletedAt);

public sealed record NotificationQueueSummary(bool Enabled, int Pending, int Processing, int Retrying,
    int TerminalFailures, int Completed, double? OldestPendingAgeSeconds, int UnfinishedLeagueBatches,
    IReadOnlyList<NotificationJobFailure> RecentFailures);

public sealed class NotificationQueueDiagnostics(AppDbContext db, TimeProvider clock,
    IOptions<NotificationWorkerOptions> options)
{
    public async Task<NotificationQueueSummary> ReadAsync(CancellationToken token)
    {
        var counts = await db.NotificationJobs.AsNoTracking().GroupBy(value => value.Status)
            .Select(group => new { Status = group.Key, Count = group.Count() }).ToDictionaryAsync(value => value.Status, value => value.Count, token);
        var retrying = await db.NotificationJobs.CountAsync(value => value.Status == NotificationJobStatus.Pending && value.AttemptCount > 0, token);
        var oldest = await db.NotificationJobs.Where(value => value.Status == NotificationJobStatus.Pending || value.Status == NotificationJobStatus.Processing)
            .MinAsync(value => (DateTime?)value.CreatedAt, token);
        var batches = await db.LeagueNotificationBatches.CountAsync(value => value.CompletedAt == null, token);
        var failed = await db.NotificationJobs.AsNoTracking().Where(value => value.Status == NotificationJobStatus.Failed)
            .OrderByDescending(value => value.CompletedAt).ThenBy(value => value.Id).Take(20)
            .Select(value => new NotificationJobFailure(value.Id, value.Kind, value.AttemptCount, value.LastErrorCode, value.CompletedAt)).ToArrayAsync(token);
        return new(options.Value.Enabled, counts.GetValueOrDefault(NotificationJobStatus.Pending),
            counts.GetValueOrDefault(NotificationJobStatus.Processing), retrying,
            counts.GetValueOrDefault(NotificationJobStatus.Failed), counts.GetValueOrDefault(NotificationJobStatus.Succeeded),
            oldest.HasValue ? Math.Max(0, (clock.GetUtcNow().UtcDateTime - oldest.Value).TotalSeconds) : null, batches, failed);
    }
}
