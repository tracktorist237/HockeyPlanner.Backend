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
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
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
        var job = await db.NotificationJobs.SingleAsync(value => value.Id == id, TestContext.Current.CancellationToken);
        var delivery = await db.NotificationDeliveries.Include(value => value.PushSubscription)
            .SingleAsync(value => value.NotificationId == job.NotificationId, TestContext.Current.CancellationToken);
        Assert.Equal(NotificationDeliveryStatus.EndpointInactive, delivery.Status);
        Assert.Matches("^[a-f0-9]{64}$", delivery.EndpointHash!);
        Assert.False(delivery.PushSubscription!.IsActive);
    }

    [Fact]
    public async Task PartialDelivery_RetryDoesNotRepeatSuccessfulEndpoint()
    {
        var token = TestContext.Current.CancellationToken;
        var id = await SeedAsync();
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var userId = await db.NotificationJobs.Where(value => value.Id == id).Select(value => value.Notification!.UserId).SingleAsync(token);
            db.PushSubscriptions.Add(new PushSubscription { UserId = userId, Endpoint = $"https://push.test.invalid/{Guid.NewGuid():N}", AuthKey = "test", P256dhKey = "test" });
            await db.SaveChangesAsync(token);
        }
        var sequence = 0;
        var push = new FakePush { Send = _ => Task.FromResult(++sequence == 2
            ? new WebPushSendResult { IsTransient = true } : new WebPushSendResult { IsSuccess = true }) };
        await ProcessAsync(id, push);
        Assert.Equal(NotificationJobStatus.Pending, (await ReadAsync(id)).Status);
        await ProcessAsync(id, push, Now.AddMinutes(1));
        Assert.Equal(3, push.Calls);
        Assert.Equal(NotificationJobStatus.Succeeded, (await ReadAsync(id)).Status);
        await using var verification = factory.Services.CreateAsyncScope();
        var context = verification.ServiceProvider.GetRequiredService<AppDbContext>();
        var notificationId = (await ReadAsync(id)).NotificationId;
        Assert.Equal(2, await context.NotificationDeliveries.CountAsync(value => value.NotificationId == notificationId && value.Status == NotificationDeliveryStatus.Sent, token));
    }

    [Fact]
    public async Task CrashAfterEndpointAcknowledgement_DoesNotResendOnRecovery()
    {
        var token = TestContext.Current.CancellationToken;
        var id = await SeedAsync();
        var push = new FakePush();
        await ProcessAsync(id, push);
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            await db.NotificationJobs.Where(value => value.Id == id).ExecuteUpdateAsync(update => update
                .SetProperty(value => value.Status, NotificationJobStatus.Processing)
                .SetProperty(value => value.CompletedAt, (DateTime?)null)
                .SetProperty(value => value.ClaimedAt, Now.AddHours(-1).UtcDateTime), token);
        }
        await ProcessAsync(id, push);
        Assert.Equal(1, push.Calls);
        Assert.Equal(NotificationJobStatus.Succeeded, (await ReadAsync(id)).Status);
    }

    [Fact]
    public async Task ExhaustedStaleClaim_IsTerminalWithoutSending()
    {
        var token = TestContext.Current.CancellationToken;
        var id = await SeedAsync(NotificationJobStatus.Processing);
        await using (var scope = factory.Services.CreateAsyncScope())
            await scope.ServiceProvider.GetRequiredService<AppDbContext>().NotificationJobs.Where(value => value.Id == id)
                .ExecuteUpdateAsync(update => update.SetProperty(value => value.AttemptCount, settings.MaxAttempts), token);
        var push = new FakePush();
        await ProcessAsync(id, push);
        Assert.Equal(0, push.Calls);
        var job = await ReadAsync(id);
        Assert.Equal(NotificationJobStatus.Failed, job.Status);
        Assert.Equal("attempts_exhausted", job.LastErrorCode);
        Assert.Null(job.ClaimId);
    }

    [Fact]
    public async Task PreferencesDisabled_SkipsPushButKeepsInAppNotification()
    {
        var token = TestContext.Current.CancellationToken;
        var id = await SeedAsync();
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var notification = await db.NotificationJobs.Where(value => value.Id == id).Select(value => value.Notification!).SingleAsync(token);
            notification.Category = NotificationCategory.AttendanceRequired;
            var preference = await db.NotificationPreferences.SingleAsync(value => value.UserId == notification.UserId, token);
            preference.AttendanceRequiredEnabled = false;
            await db.SaveChangesAsync(token);
        }
        var push = new FakePush();
        await ProcessAsync(id, push);
        Assert.Equal(0, push.Calls);
        Assert.Equal(NotificationJobStatus.Succeeded, (await ReadAsync(id)).Status);
        await using var verification = factory.Services.CreateAsyncScope();
        var context = verification.ServiceProvider.GetRequiredService<AppDbContext>();
        var notificationId = (await ReadAsync(id)).NotificationId;
        Assert.True(await context.Notifications.AnyAsync(value => value.Id == notificationId, token));
        Assert.Equal("preferences_disabled", (await context.NotificationDeliveries.SingleAsync(value => value.NotificationId == notificationId, token)).Error);
    }

    [Theory]
    [InlineData(false, NotificationJobStatus.Succeeded)]
    [InlineData(true, NotificationJobStatus.Pending)]
    public async Task UnconfiguredPush_OnlyRetriesWhenUserHasActiveSubscription(bool subscribed, NotificationJobStatus expected)
    {
        var token = TestContext.Current.CancellationToken;
        var id = await SeedAsync();
        if (!subscribed)
        {
            await using var scope = factory.Services.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var userId = await db.NotificationJobs.Where(value => value.Id == id).Select(value => value.Notification!.UserId).SingleAsync(token);
            await db.PushSubscriptions.Where(value => value.UserId == userId).ExecuteDeleteAsync(token);
        }
        var push = new FakePush { Configured = false };
        await ProcessAsync(id, push);
        Assert.Equal(0, push.Calls);
        Assert.Equal(expected, (await ReadAsync(id)).Status);
    }

    private async Task<Guid> SeedAsync(NotificationJobStatus status = NotificationJobStatus.Pending)
    {
        var scenario = await TwoUserNotificationScenarioBuilder.CreateAsync(factory.Services, TestContext.Current.CancellationToken);
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        // Fixture notification category may differ; explicitly enable all for delivery tests.
        var preferences = await db.NotificationPreferences.SingleAsync(value => value.UserId == scenario.UserA.Id, TestContext.Current.CancellationToken);
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
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
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
        return await scope.ServiceProvider.GetRequiredService<AppDbContext>().NotificationJobs.AsNoTracking().SingleAsync(value => value.Id == id, TestContext.Current.CancellationToken);
    }

    private sealed class FakePush : IWebPushService
    {
        public bool Configured = true;
        public bool IsConfigured => Configured;
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
