using HockeyPlanner.Backend.Core.Entities;
using HockeyPlanner.Backend.Core.Enums;
using HockeyPlanner.Backend.Infrastructure.Data;
using HockeyPlanner.Backend.IntegrationTests.Infrastructure;
using HockeyPlanner.Backend.WebAPI.Models.ExternalLeagues;
using HockeyPlanner.Backend.WebAPI.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace HockeyPlanner.Backend.IntegrationTests.Services;

[Collection(IntegrationTestCollection.Name)]
public sealed class LeagueNotificationBatchTests(HockeyPlannerWebApplicationFactory factory)
{
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(3)]
    public async Task CrashedBatch_RecoversOneAggregatedNotificationPerRecipient(int count)
    {
        var token = TestContext.Current.CancellationToken;
        Guid teamId, userId;
        var events = Enumerable.Range(0, count).Select(index => new ExternalCreatedEvent { EventId = Guid.NewGuid(), Title = $"Match {index}" }).ToArray();
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var user = new User { FirstName = "Batch", LastName = "Owner" };
            var team = new Team { Name = "Batch team", InviteCode = Guid.NewGuid().ToString("N")[..20], CreatedByUserId = user.Id };
            db.AddRange(user, team, new TeamMembership { TeamId = team.Id, UserId = user.Id, Role = TeamMemberRole.Owner });
            await db.SaveChangesAsync(token);
            teamId = team.Id; userId = user.Id;
            var batches = Create(db);
            await using (await batches.BeginAsync(teamId, true, token))
            {
                // Two links can observe the same event; the aggregate is by EventId.
                await using var tx = await db.Database.BeginTransactionAsync(token);
                await batches.RecordAsync(events, [], token);
                await db.SaveChangesAsync(token);
                await batches.RecordAsync(events, [], token);
                await db.SaveChangesAsync(token);
                await tx.CommitAsync(token);
                await using var competing = factory.Services.CreateAsyncScope();
                await Create(competing.ServiceProvider.GetRequiredService<AppDbContext>()).RecoverAsync(100, token);
                Assert.False(await db.Notifications.AnyAsync(value => value.UserId == userId, token));
                // No CompleteAsync: simulate process termination after link commits.
            }
        }
        await using var recoveredScope = factory.Services.CreateAsyncScope();
        var recovered = recoveredScope.ServiceProvider.GetRequiredService<AppDbContext>();
        await Create(recovered).RecoverAsync(100, token);
        await Create(recovered).RecoverAsync(100, token);
        var notifications = await recovered.Notifications.Where(value => value.UserId == userId).ToArrayAsync(token);
        if (count == 0) Assert.Empty(notifications);
        else
        {
            var notification = Assert.Single(notifications);
            Assert.Equal(count == 1 ? "Новое мероприятие" : "Новые мероприятия", notification.Title);
            Assert.Equal(count == 1 ? $"/events/{events[0].EventId}" : "/events", notification.Url);
            Assert.Equal(count == 1 ? "Match 0: отметьтесь, сможете ли быть."
                : $"Появилось {count} новых мероприятий из лиги. Отметьтесь, сможете ли быть.", notification.Body);
            Assert.Single(await recovered.NotificationJobs.Where(value => value.NotificationId == notification.Id).ToArrayAsync(token));
        }
        Assert.NotNull((await recovered.LeagueNotificationBatches.SingleAsync(value => value.TeamId == teamId, token)).CompletedAt);
    }

    [Theory]
    [InlineData(false, 0)]
    [InlineData(true, 1)]
    public async Task RescheduleIntent_IsDurableOnlyForBackground(bool background, int expected)
    {
        var token = TestContext.Current.CancellationToken;
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var user = new User { FirstName = "Reschedule", LastName = "Owner" };
        var team = new Team { Name = "Team", InviteCode = Guid.NewGuid().ToString("N")[..20], CreatedByUserId = user.Id };
        db.AddRange(user, team, new TeamMembership { TeamId = team.Id, UserId = user.Id, Role = TeamMemberRole.Owner });
        await db.SaveChangesAsync(token);
        var batches = Create(db);
        await using var handle = await batches.BeginAsync(team.Id, background, token);
        var change = new ExternalEventChange { EventId = Guid.NewGuid(), Title = "Match", NewStartTime = new DateTime(2026, 10, 1, 17, 30, 0, DateTimeKind.Utc), NewStatus = EventStatus.Rescheduled };
        await using (var tx = await db.Database.BeginTransactionAsync(token))
        {
            await batches.RecordAsync([], [change, change], token);
            await db.SaveChangesAsync(token);
            await tx.CommitAsync(token);
        }
        await batches.CompleteAsync(token);
        var notifications = await db.Notifications.Where(value => value.UserId == user.Id).ToArrayAsync(token);
        Assert.Equal(expected, notifications.Length);
        if (expected > 0)
        {
            Assert.Equal("Матч перенесён", notifications[0].Title);
            Assert.Contains("20:30", notifications[0].Body);
            Assert.Equal($"/events/{change.EventId}", notifications[0].Url);
        }
    }

    private static LeagueNotificationBatches Create(AppDbContext db) => new(db,
        new NotificationOutbox(db, TimeProvider.System, NullLogger<NotificationOutbox>.Instance), TimeProvider.System);
}
