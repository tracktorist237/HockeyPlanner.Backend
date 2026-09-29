using HockeyPlanner.Backend.Core.Enums;
using HockeyPlanner.Backend.Infrastructure.Data;
using HockeyPlanner.Backend.IntegrationTests.Fixtures;
using HockeyPlanner.Backend.IntegrationTests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace HockeyPlanner.Backend.IntegrationTests.Services;

[Collection(IntegrationTestCollection.Name)]
public sealed class NotificationOutboxTests(HockeyPlannerWebApplicationFactory factory)
{
    [Fact]
    public async Task ConcurrentEnqueue_LogicalIdentityCreatesOneNotificationAndJobPerUser()
    {
        var scenario = await TwoUserNotificationScenarioBuilder.CreateAsync(factory.Services, TestContext.Current.CancellationToken);
        var key = Guid.NewGuid().ToString("N");
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var arrivals = 0;
        async Task Enqueue(bool rendezvous = false)
        {
            await using var scope = factory.Services.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            if (rendezvous)
            {
                await db.Database.OpenConnectionAsync(TestContext.Current.CancellationToken);
                if (Interlocked.Increment(ref arrivals) == 2) ready.SetResult();
                await release.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
            }
            await new NotificationOutbox(db, TimeProvider.System, NullLogger<NotificationOutbox>.Instance)
                .EnqueueAsync([scenario.UserA.Id, scenario.UserA.Id, scenario.UserB.Id], key,
                    NotificationType.EventPublished, NotificationCategory.AttendanceRequired, "Title", "Body", "/events", TestContext.Current.CancellationToken);
        }
        var concurrent = Task.WhenAll(Enqueue(true), Enqueue(true));
        try { await ready.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken); }
        finally
        {
            release.TrySetResult();
            await concurrent;
        }
        await Enqueue();
        await using var verification = factory.Services.CreateAsyncScope();
        var context = verification.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.Equal(2, await context.Notifications.CountAsync(value => value.LogicalKey == key, TestContext.Current.CancellationToken));
        Assert.Equal(2, await context.NotificationJobs.CountAsync(value => value.Notification!.LogicalKey == key, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task OuterTransactionRollback_RemovesBusinessChangeNotificationAndJob()
    {
        var scenario = await TwoUserNotificationScenarioBuilder.CreateAsync(factory.Services, TestContext.Current.CancellationToken);
        var key = Guid.NewGuid().ToString("N");
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            await using var tx = await db.Database.BeginTransactionAsync(TestContext.Current.CancellationToken);
            var user = await db.Users.SingleAsync(value => value.Id == scenario.UserA.Id, TestContext.Current.CancellationToken);
            user.FirstName = "Must roll back";
            await new NotificationOutbox(db, TimeProvider.System, NullLogger<NotificationOutbox>.Instance)
                .EnqueueAsync([user.Id], key, NotificationType.EventPublished, NotificationCategory.AttendanceRequired,
                    "Title", "Body", null, TestContext.Current.CancellationToken);
            await tx.RollbackAsync(TestContext.Current.CancellationToken);
        }
        await using var verification = factory.Services.CreateAsyncScope();
        var context = verification.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.False(await context.Notifications.AnyAsync(value => value.LogicalKey == key, TestContext.Current.CancellationToken));
        Assert.False(await context.NotificationJobs.AnyAsync(value => value.Notification!.LogicalKey == key, TestContext.Current.CancellationToken));
        Assert.NotEqual("Must roll back", (await context.Users.SingleAsync(value => value.Id == scenario.UserA.Id, TestContext.Current.CancellationToken)).FirstName);
    }
}
