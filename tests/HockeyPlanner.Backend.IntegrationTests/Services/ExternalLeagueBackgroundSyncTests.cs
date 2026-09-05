using HockeyPlanner.Backend.Core.Exceptions;
using HockeyPlanner.Backend.IntegrationTests.Infrastructure;
using HockeyPlanner.Backend.WebAPI.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace HockeyPlanner.Backend.IntegrationTests.Services;

public sealed class ExternalLeagueSyncScheduleTests
{
    [Theory]
    [InlineData("2026-09-04T01:59:00Z", false)] // 04:59 MSK
    [InlineData("2026-09-04T02:01:00Z", true)]  // 05:01 MSK
    [InlineData("2026-09-04T08:00:00Z", true)]  // catch-up at 11:00 MSK
    public void DailyThreshold_IsMoscowBasedAndSupportsCatchUp(string utc, bool expectedDue)
    {
        var threshold = ExternalLeagueSyncSchedule.GetCurrentThresholdUtc(DateTimeOffset.Parse(utc), 5);
        Assert.Equal(expectedDue, threshold.HasValue);
        if (threshold.HasValue) Assert.Equal(DateTimeOffset.Parse("2026-09-04T02:00:00Z"), threshold.Value);
    }

    [Fact]
    public void DueState_UsesEveryPersistentLinkTimestamp()
    {
        var now = DateTimeOffset.Parse("2026-09-04T07:00:00Z");
        Assert.False(ExternalLeagueSyncSchedule.IsTeamDue(now, 5, []));
        Assert.False(ExternalLeagueSyncSchedule.IsTeamDue(now, 5, [DateTime.Parse("2026-09-04T02:30:00Z").ToUniversalTime()]));
        Assert.True(ExternalLeagueSyncSchedule.IsTeamDue(now, 5, [DateTime.Parse("2026-09-03T17:00:00Z").ToUniversalTime()]));
        Assert.True(ExternalLeagueSyncSchedule.IsTeamDue(now, 5, [DateTime.Parse("2026-09-04T02:30:00Z").ToUniversalTime(), null]));
    }

    [Fact]
    public async Task Retry_IsBoundedForTransientFailures()
    {
        var calls = 0;
        var result = await ExternalLeagueBackgroundSyncWorker.ExecuteWithRetryAsync(
            _ => ++calls < 3 ? Task.FromException<int>(new HttpRequestException("temporary")) : Task.FromResult(42),
            new ExternalLeagueSyncOptions { MaxRetryAttempts = 2, RetryDelaySeconds = 0 },
            CancellationToken.None);
        Assert.Equal(42, result);
        Assert.Equal(3, calls);
    }

    [Fact]
    public async Task Retry_DoesNotRetryBusinessRulesOrShutdownCancellation()
    {
        var businessCalls = 0;
        await Assert.ThrowsAsync<BusinessRuleException>(() => ExternalLeagueBackgroundSyncWorker.ExecuteWithRetryAsync<int>(
            _ => { businessCalls++; throw new BusinessRuleException("invalid"); },
            new ExternalLeagueSyncOptions { MaxRetryAttempts = 3, RetryDelaySeconds = 0 },
            CancellationToken.None));
        Assert.Equal(1, businessCalls);

        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ExternalLeagueBackgroundSyncWorker.ExecuteWithRetryAsync<int>(
            token => Task.FromCanceled<int>(token),
            new ExternalLeagueSyncOptions { MaxRetryAttempts = 3, RetryDelaySeconds = 0 },
            cancellation.Token));
    }

    [Fact]
    public async Task Retry_StopsAfterConfiguredTransientAttempts()
    {
        var calls = 0;
        await Assert.ThrowsAsync<HttpRequestException>(() => ExternalLeagueBackgroundSyncWorker.ExecuteWithRetryAsync<int>(
            _ => { calls++; throw new HttpRequestException("still unavailable"); },
            new ExternalLeagueSyncOptions { MaxRetryAttempts = 2, RetryDelaySeconds = 0 },
            CancellationToken.None));
        Assert.Equal(3, calls);
    }

    [Fact]
    public async Task BoundedExecution_NeverExceedsConfiguredParallelism()
    {
        var active = 0;
        var maximum = 0;
        var twoStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await ExternalLeagueBackgroundSyncWorker.ForEachBoundedAsync(
            new[] { 1, 2, 3 },
            2,
            async (_, _) =>
            {
                var current = Interlocked.Increment(ref active);
                maximum = Math.Max(maximum, current);
                if (current == 2) twoStarted.TrySetResult();
                await twoStarted.Task;
                release.TrySetResult();
                await release.Task;
                Interlocked.Decrement(ref active);
            },
            CancellationToken.None);
        Assert.Equal(2, maximum);
    }

    [Fact]
    public async Task DisabledWorker_DoesNotCreateAScopeOrSync()
    {
        var worker = new ExternalLeagueBackgroundSyncWorker(
            null!,
            new StaticOptionsMonitor<ExternalLeagueSyncOptions>(new ExternalLeagueSyncOptions { Enabled = false }),
            TimeProvider.System,
            NullLogger<ExternalLeagueBackgroundSyncWorker>.Instance);
        await worker.RunOnceAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public void RescheduledNotification_UsesNewMoscowTimeAndHockeyPlannerEventUrl()
    {
        var eventId = Guid.NewGuid();
        var change = new HockeyPlanner.Backend.WebAPI.Models.ExternalLeagues.ExternalEventChange
        {
            EventId = eventId,
            Title = "Северная столица — АЛГА",
            NewStartTime = DateTime.Parse("2026-09-08T17:30:00Z").ToUniversalTime()
        };
        Assert.Equal("Северная столица — АЛГА перенесён на 8 сентября, 20:30.", ExternalLeagueBackgroundSyncWorker.BuildRescheduledBody(change));
        Assert.Equal($"/events/{eventId}", ExternalLeagueBackgroundSyncWorker.BuildEventUrl(eventId));
    }
}

[Collection(IntegrationTestCollection.Name)]
public sealed class PostgresTeamSyncLockTests(HockeyPlannerWebApplicationFactory factory)
{
    [Fact]
    public async Task AdvisoryLock_AllowsOneOwnerAndIsReleased()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var teamId = Guid.NewGuid();
        await using var scopeA = factory.Services.CreateAsyncScope();
        await using var scopeB = factory.Services.CreateAsyncScope();
        var lockA = scopeA.ServiceProvider.GetRequiredService<IExternalLeagueTeamLock>();
        var lockB = scopeB.ServiceProvider.GetRequiredService<IExternalLeagueTeamLock>();

        var handleA = await lockA.TryAcquireAsync(teamId, cancellationToken);
        Assert.NotNull(handleA);
        Assert.Null(await lockB.TryAcquireAsync(teamId, cancellationToken));
        await handleA!.DisposeAsync();
        await using var handleB = await lockB.TryAcquireAsync(teamId, cancellationToken);
        Assert.NotNull(handleB);
    }
}

internal sealed class StaticOptionsMonitor<T>(T value) : IOptionsMonitor<T>
{
    public T CurrentValue => value;
    public T Get(string? name) => value;
    public IDisposable? OnChange(Action<T, string?> listener) => null;
}
