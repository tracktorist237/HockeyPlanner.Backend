using HockeyPlanner.Backend.Application.Abstractions.Services;
using HockeyPlanner.Backend.Core.Enums;
using HockeyPlanner.Backend.Core.Exceptions;
using HockeyPlanner.Backend.Infrastructure.Data;
using HockeyPlanner.Backend.WebAPI.Models.ExternalLeagues;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace HockeyPlanner.Backend.WebAPI.Services;

public sealed class ExternalLeagueBackgroundSyncWorker(
    IServiceScopeFactory scopeFactory,
    IOptionsMonitor<ExternalLeagueSyncOptions> options,
    TimeProvider timeProvider,
    ILogger<ExternalLeagueBackgroundSyncWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try { await RunOnceAsync(stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception exception) { logger.LogError(exception, "Background external league sync pass failed."); }

            var minutes = Math.Max(1, options.CurrentValue.PollIntervalMinutes);
            await Task.Delay(TimeSpan.FromMinutes(minutes), timeProvider, stoppingToken);
        }
    }

    public async Task RunOnceAsync(CancellationToken cancellationToken)
    {
        var settings = options.CurrentValue;
        if (!settings.Enabled) return;
        var threshold = ExternalLeagueSyncSchedule.GetCurrentThresholdUtc(timeProvider.GetUtcNow(), Math.Clamp(settings.DailySyncHour, 0, 23));
        if (!threshold.HasValue) return;

        Guid[] teamIds;
        using (var scope = scopeFactory.CreateScope())
        {
            var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            teamIds = await context.TeamExternalLeagueLinks.AsNoTracking()
                .Where(link => !link.LastSuccessfulSyncAt.HasValue || link.LastSuccessfulSyncAt < threshold.Value.UtcDateTime)
                .Select(link => link.TeamId)
                .Distinct()
                .ToArrayAsync(cancellationToken);
        }

        await ForEachBoundedAsync(
            teamIds,
            settings.MaxParallelTeams,
            (teamId, token) => SyncTeamAsync(teamId, threshold.Value.UtcDateTime, settings, token),
            cancellationToken);
        logger.LogInformation("Background external league sync completed: TeamsConsidered {TeamsConsidered}", teamIds.Length);
    }

    private async Task SyncTeamAsync(Guid teamId, DateTime thresholdUtc, ExternalLeagueSyncOptions settings, CancellationToken cancellationToken)
    {
        using var scope = scopeFactory.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var teamLock = scope.ServiceProvider.GetRequiredService<IExternalLeagueTeamLock>();
        await using var handle = await teamLock.TryAcquireAsync(teamId, cancellationToken);
        if (handle is null)
        {
            logger.LogInformation("Team skipped because distributed lock is held: TeamId {TeamId}", teamId);
            return;
        }

        var linkIds = await context.TeamExternalLeagueLinks.AsNoTracking()
            .Where(link => link.TeamId == teamId && (!link.LastSuccessfulSyncAt.HasValue || link.LastSuccessfulSyncAt < thresholdUtc))
            .OrderBy(link => link.CreatedAt)
            .Select(link => link.Id)
            .ToArrayAsync(cancellationToken);
        if (linkIds.Length == 0) return;

        var sync = scope.ServiceProvider.GetRequiredService<IExternalLeagueSyncService>();
        var notifications = scope.ServiceProvider.GetRequiredService<INotificationService>();
        var createdEventNotifier = scope.ServiceProvider.GetRequiredService<IExternalLeagueCreatedEventNotifier>();
        var createdEvents = new List<ExternalCreatedEvent>();
        var failed = 0;
        foreach (var linkId in linkIds)
        {
            if (cancellationToken.IsCancellationRequested) break;
            try
            {
                var result = await ExecuteWithRetryAsync(
                    token => sync.SyncExternalLinkAsync(linkId, token),
                    settings,
                    cancellationToken);
                createdEvents.AddRange(result.CreatedEvents);
                foreach (var change in result.Changes.Where(change => change.NewStatus == EventStatus.Rescheduled))
                {
                    await notifications.NotifyTeamAsync(
                        teamId,
                        NotificationType.EventRescheduled,
                        NotificationCategory.EventUpdates,
                        "Матч перенесён",
                        BuildRescheduledBody(change),
                        BuildEventUrl(change.EventId),
                        cancellationToken);
                }
                logger.LogInformation(
                    "Background external league link synchronized: TeamId {TeamId}, LinkId {LinkId}, Provider {Provider}, Created {Created}, Updated {Updated}, Reschedules {Reschedules}",
                    teamId, linkId, result.Provider, result.CreatedCount, result.UpdatedCount, result.Changes.Count);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception exception)
            {
                failed++;
                logger.LogWarning(exception, "Background external league link sync failed: TeamId {TeamId}, LinkId {LinkId}", teamId, linkId);
            }
        }
        await createdEventNotifier.NotifyAsync(teamId, createdEvents, cancellationToken);
        logger.LogInformation("Background external league team sync finished: TeamId {TeamId}, Links {Links}, Failed {Failed}", teamId, linkIds.Length, failed);
    }

    public static async Task<T> ExecuteWithRetryAsync<T>(
        Func<CancellationToken, Task<T>> operation,
        ExternalLeagueSyncOptions settings,
        CancellationToken cancellationToken)
    {
        var retries = Math.Max(0, settings.MaxRetryAttempts);
        for (var attempt = 0; ; attempt++)
        {
            try { return await operation(cancellationToken); }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception exception) when (IsTransient(exception) && attempt < retries)
            {
                var delay = TimeSpan.FromSeconds(Math.Max(0, settings.RetryDelaySeconds) * Math.Pow(2, attempt));
                if (delay > TimeSpan.Zero) await Task.Delay(delay, cancellationToken);
            }
        }
    }

    public static async Task ForEachBoundedAsync<T>(
        IReadOnlyCollection<T> items,
        int maxParallel,
        Func<T, CancellationToken, Task> operation,
        CancellationToken cancellationToken)
    {
        using var gate = new SemaphoreSlim(Math.Max(1, maxParallel));
        var tasks = items.Select(async item =>
        {
            await gate.WaitAsync(cancellationToken);
            try { await operation(item, cancellationToken); }
            finally { gate.Release(); }
        });
        await Task.WhenAll(tasks);
    }

    private static bool IsTransient(Exception exception) =>
        exception is HttpRequestException or TimeoutException or TaskCanceledException;

    public static string BuildEventUrl(Guid eventId) => $"/events/{eventId}";

    public static string BuildRescheduledBody(ExternalEventChange change)
    {
        var local = TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(change.NewStartTime, DateTimeKind.Utc), ExternalLeagueSyncSchedule.ResolveMoscowTimeZone());
        var date = local.ToString("d MMMM", System.Globalization.CultureInfo.GetCultureInfo("ru-RU"));
        return $"{change.Title} перенесён на {date}, {local:HH:mm}.";
    }
}
