using System.Net.Http.Json;
using HockeyPlanner.Backend.Application.Abstractions.Services;
using HockeyPlanner.Backend.Core.Entities;
using HockeyPlanner.Backend.Core.Enums;
using HockeyPlanner.Backend.Infrastructure.Data;
using HockeyPlanner.Backend.IntegrationTests.Infrastructure;
using HockeyPlanner.Backend.Shared.Models.Events;
using HockeyPlanner.Backend.Shared.Models.Teams;
using HockeyPlanner.Backend.WebAPI.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace HockeyPlanner.Backend.IntegrationTests.Services;

[Collection(IntegrationTestCollection.Name)]
public sealed class NotificationProducerDurabilityTests(HockeyPlannerWebApplicationFactory factory)
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task EventAndTeamNews_CommitDurableJobsWithoutCallingProvider(bool news)
    {
        var token = TestContext.Current.CancellationToken;
        var push = new RejectPush();
        using var app = factory.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
        {
            services.RemoveAll<IWebPushService>();
            services.AddSingleton<IWebPushService>(push);
        }));
        var owner = new User { FirstName = "Durable", LastName = "Owner" };
        var team = new Team { Name = "Durable", InviteCode = Guid.NewGuid().ToString("N")[..20], CreatedByUserId = owner.Id };
        await using (var setup = app.Services.CreateAsyncScope())
        {
            var db = setup.ServiceProvider.GetRequiredService<AppDbContext>();
            db.AddRange(owner, team, new TeamMembership { TeamId = team.Id, UserId = owner.Id, Role = TeamMemberRole.Owner });
            await db.SaveChangesAsync(token);
        }
        Guid? eventId = null;
        if (news)
        {
            using var client = app.CreateClient();
            await using var auth = app.Services.CreateAsyncScope();
            var accessToken = auth.ServiceProvider.GetRequiredService<IAuthTokenService>().CreateAccessToken(owner);
            client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", accessToken);
            using var response = await client.PostAsJsonAsync($"/api/teams/{team.Id}/news?currentUserId={owner.Id}",
                new CreateTeamNewsRequest { Title = "Team news", Body = "Body", SendNotification = true }, token);
            response.EnsureSuccessStatusCode();
        }
        else
        {
            await using var scope = app.Services.CreateAsyncScope();
            eventId = await scope.ServiceProvider.GetRequiredService<IEventService>().CreateEvent(new CreateEventDto
            { TeamId = team.Id, Title = "Match", Type = EventType.Game, StartTime = DateTime.UtcNow.AddDays(1), LocationName = "Arena", LocationAddress = "Address" }, owner.Id, token);
        }
        Assert.Equal(0, push.Calls);
        await using var verification = app.Services.CreateAsyncScope();
        var context = verification.ServiceProvider.GetRequiredService<AppDbContext>();
        var notification = await context.Notifications.SingleAsync(value => value.UserId == owner.Id, token);
        Assert.Equal(news ? NotificationType.TeamNewsCreated : NotificationType.EventPublished, notification.Type);
        Assert.Equal(news ? $"/teams/{team.Id}" : $"/events/{eventId}", notification.Url);
        if (news) Assert.True(await context.TeamNews.AnyAsync(value => value.TeamId == team.Id, token));
        else
        {
            Assert.True(await context.Events.AnyAsync(value => value.Id == eventId, token));
            Assert.True(await context.Attendances.AnyAsync(value => value.EventId == eventId && value.UserId == owner.Id, token));
        }
        Assert.Equal(NotificationJobStatus.Pending, (await context.NotificationJobs.SingleAsync(value => value.NotificationId == notification.Id, token)).Status);
    }

    private sealed class RejectPush : IWebPushService
    {
        public bool IsConfigured => true;
        public int Calls;
        public Task<WebPushSendResult> SendAsync(PushSubscription subscription, object payload, CancellationToken cancellationToken = default)
        { Calls++; throw new HttpRequestException("Provider outage"); }
    }
}
