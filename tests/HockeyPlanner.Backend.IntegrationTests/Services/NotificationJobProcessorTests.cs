using HockeyPlanner.Backend.Core.Entities;
using HockeyPlanner.Backend.Core.Enums;
using HockeyPlanner.Backend.Infrastructure.Data;
using HockeyPlanner.Backend.IntegrationTests.Fixtures;
using HockeyPlanner.Backend.IntegrationTests.Infrastructure;
using HockeyPlanner.Backend.WebAPI.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace HockeyPlanner.Backend.IntegrationTests.Services;

[Collection(IntegrationTestCollection.Name)]
public sealed class NotificationJobProcessorTests(HockeyPlannerWebApplicationFactory factory)
{
    private static readonly DateTimeOffset Now = new(2026, 10, 1, 8, 0, 0, TimeSpan.Zero);
    private readonly NotificationWorkerOptions settings = new() { MaxAttempts = 2, RetryDelaySeconds = 10 };

    [Fact]
    public async Task Success_ThenDuplicateProcessing_SendsOnce()
    {
        var id = await SeedAsync();
        var push = new FakePush();
        await ProcessAsync(id, push);
        await ProcessAsync(id, push);
        Assert.Equal(1, push.Calls);
        Assert.Equal(NotificationJobStatus.Succeeded, (await ReadAsync(id)).Status);
    }

    [Fact]
    public async Task TransientFailure_Backoff_ThenSuccess()
    {
        var id = await SeedAsync();
        var push = new FakePush { Send = _ => throw new HttpRequestException("secret upstream payload") };
        await ProcessAsync(id, push);
        var retry = await ReadAsync(id);
        Assert.Equal(NotificationJobStatus.Pending, retry.Status);
        Assert.Equal(Now.UtcDateTime.AddSeconds(10), retry.NextAttemptAt);
        Assert.DoesNotContain("secret", retry.LastErrorCode!);
        await ProcessAsync(id, push);
        Assert.Equal(1, push.Calls);
        push.Send = _ => Task.FromResult(new WebPushSendResult { IsSuccess = true });
        await ProcessAsync(id, push, Now.AddSeconds(10));
        Assert.Equal(2, push.Calls);
        Assert.Equal(NotificationJobStatus.Succeeded, (await ReadAsync(id)).Status);
    }

    [Theory]
    [InlineData(true, 2)]
    [InlineData(false, 1)]
    public async Task Failures_AreBounded(bool transient, int expectedCalls)
    {
        var id = await SeedAsync();
        var push = new FakePush { Send = _ => Task.FromResult(new WebPushSendResult { IsTransient = transient }) };
        await ProcessAsync(id, push);
        await ProcessAsync(id, push, Now.AddMinutes(1));
        await ProcessAsync(id, push, Now.AddMinutes(2));
        Assert.Equal(expectedCalls, push.Calls);
        Assert.Equal(NotificationJobStatus.Failed, (await ReadAsync(id)).Status);
    }

    [Fact]
    public async Task StaleClaim_IsRecoveredInAnotherScope()
    {
        var id = await SeedAsync(NotificationJobStatus.Processing);
        var push = new FakePush();
        await ProcessAsync(id, push);
        Assert.Equal(1, push.Calls);
        Assert.Equal(NotificationJobStatus.Succeeded, (await ReadAsync(id)).Status);
    }

    [Fact]
    public async Task ConcurrentScopes_CannotSendSameJobEvenAfterClaimTimeout()
    {
        var id = await SeedAsync();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var push = new FakePush { Send = async token =>
        {
            entered.SetResult();
            await release.Task.WaitAsync(token);
            return new WebPushSendResult { IsSuccess = true };
        }};
        var first = ProcessAsync(id, push);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        try { await ProcessAsync(id, push, Now.AddHours(1)); }
        finally { release.SetResult(); }
        await first;
        Assert.Equal(1, push.Calls);
    }

    [Fact]
    public async Task Cancellation_LeavesRecoverableClaimAndReleasesLock()
    {
        var id = await SeedAsync();
        using var cancel = new CancellationTokenSource();
        var push = new FakePush { Send = token => { cancel.Cancel(); token.ThrowIfCancellationRequested(); return Task.FromResult(new WebPushSendResult()); } };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ProcessAsync(id, push, token: cancel.Token));
        Assert.Equal(NotificationJobStatus.Processing, (await ReadAsync(id)).Status);
        await ProcessAsync(id, new FakePush(), Now.AddMinutes(10));
        Assert.Equal(NotificationJobStatus.Succeeded, (await ReadAsync(id)).Status);
    }

    [Fact]
    public async Task ProviderGone_RevokesSubscriptionWithoutRetry()
    {
        var id = await SeedAsync();
        var push = new FakePush { Send = _ => Task.FromResult(new WebPushSendResult { ShouldRemoveSubscription = true }) };
        await ProcessAsync(id, push);
        Assert.Equal(NotificationJobStatus.Succeeded, (await ReadAsync(id)).Status);
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var job = await db.NotificationJobs.SingleAsync(value => value.Id == id);
        var delivery = await db.NotificationDeliveries.Include(value => value.PushSubscription)
            .SingleAsync(value => value.NotificationId == job.NotificationId);
        Assert.Equal(NotificationDeliveryStatus.EndpointInactive, delivery.Status);
        Assert.False(delivery.PushSubscription!.IsActive);
    }

    private async Task<Guid> SeedAsync(NotificationJobStatus status = NotificationJobStatus.Pending)
    {
        var scenario = await TwoUserNotificationScenarioBuilder.CreateAsync(factory.Services, TestContext.Current.CancellationToken);
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        // Fixture notification category may differ; explicitly enable all for delivery tests.
        var preferences = await db.NotificationPreferences.SingleAsync(value => value.UserId == scenario.UserA.Id);
        preferences.AppUpdatesEnabled = preferences.GoaliesEnabled = preferences.RosterReadyEnabled = true;
        db.PushSubscriptions.Add(new PushSubscription
        {
            UserId = scenario.UserA.Id, Endpoint = $"https://push.test.invalid/{Guid.NewGuid():N}",
            AuthKey = "test", P256dhKey = "test"
        });
        var job = new NotificationJob
        {
            NotificationId = scenario.UserAUnread.Id, NextAttemptAt = Now.UtcDateTime,
            Status = status, ClaimedAt = status == NotificationJobStatus.Processing ? Now.AddHours(-1).UtcDateTime : null,
            ClaimId = status == NotificationJobStatus.Processing ? Guid.NewGuid() : null
        };
        db.NotificationJobs.Add(job);
        await db.SaveChangesAsync();
        return job.Id;
    }

    private async Task ProcessAsync(Guid id, FakePush push, DateTimeOffset? now = null, CancellationToken? token = null)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        push.CheckTransaction = () => Assert.Null(db.Database.CurrentTransaction);
        var processor = new NotificationJobProcessor(db, push, new FixedTimeProvider(now ?? Now),
            Options.Create(settings), NullLogger<NotificationJobProcessor>.Instance);
        await processor.ProcessAsync(id, token ?? TestContext.Current.CancellationToken);
    }

    private async Task<NotificationJob> ReadAsync(Guid id)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<AppDbContext>().NotificationJobs.AsNoTracking().SingleAsync(value => value.Id == id);
    }

    private sealed class FakePush : IWebPushService
    {
        public bool IsConfigured => true;
        public int Calls;
        public Action? CheckTransaction;
        public Func<CancellationToken, Task<WebPushSendResult>> Send = _ => Task.FromResult(new WebPushSendResult { IsSuccess = true });
        public Task<WebPushSendResult> SendAsync(PushSubscription subscription, object payload, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref Calls);
            CheckTransaction?.Invoke();
            return Send(cancellationToken);
        }
    }
}
