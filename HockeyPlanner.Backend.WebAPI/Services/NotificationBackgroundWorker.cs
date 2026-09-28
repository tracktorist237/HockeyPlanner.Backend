using HockeyPlanner.Backend.Core.Enums;
using HockeyPlanner.Backend.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace HockeyPlanner.Backend.WebAPI.Services;

public sealed class NotificationBackgroundWorker(
    IServiceScopeFactory scopes, IOptions<NotificationWorkerOptions> options,
    TimeProvider clock, ILogger<NotificationBackgroundWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try { await RunOnceAsync(stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception exception)
            {
                logger.LogError("Notification worker pass failed: ErrorType {ErrorType}", exception.GetType().Name);
            }
            try { await Task.Delay(TimeSpan.FromSeconds(options.Value.PollIntervalSeconds), clock, stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
        }
    }

    public async Task RunOnceAsync(CancellationToken token)
    {
        if (!options.Value.Enabled) return;
        Guid[] ids;
        await using (var scope = scopes.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var now = clock.GetUtcNow().UtcDateTime;
            var stale = now.AddSeconds(-options.Value.ClaimTimeoutSeconds);
            ids = await db.NotificationJobs.AsNoTracking()
                .Where(value => (value.Status == NotificationJobStatus.Pending && value.NextAttemptAt <= now)
                    || (value.Status == NotificationJobStatus.Processing && value.ClaimedAt <= stale))
                .OrderBy(value => value.NextAttemptAt).ThenBy(value => value.Id)
                .Take(options.Value.BatchSize).Select(value => value.Id).ToArrayAsync(token);
        }
        foreach (var id in ids)
        {
            token.ThrowIfCancellationRequested();
            await using var scope = scopes.CreateAsyncScope();
            try { await scope.ServiceProvider.GetRequiredService<NotificationJobProcessor>().ProcessAsync(id, token); }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
            catch (Exception exception)
            {
                logger.LogError("Notification job processing interrupted: JobId {JobId}, ErrorType {ErrorType}", id, exception.GetType().Name);
            }
        }
    }
}
