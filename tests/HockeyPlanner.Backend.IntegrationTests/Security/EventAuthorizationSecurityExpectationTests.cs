using System.Net;
using System.Net.Http.Json;
using HockeyPlanner.Backend.Core.Entities;
using HockeyPlanner.Backend.IntegrationTests.Fixtures;
using HockeyPlanner.Backend.IntegrationTests.Infrastructure;
using HockeyPlanner.Backend.Infrastructure.Data;
using HockeyPlanner.Backend.Shared.Models.Events;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace HockeyPlanner.Backend.IntegrationTests.Security;

[Collection(IntegrationTestCollection.Name)]
public sealed class EventAuthorizationSecurityExpectationTests
{
    private readonly HockeyPlannerWebApplicationFactory _application;

    public EventAuthorizationSecurityExpectationTests(HockeyPlannerWebApplicationFactory application)
    {
        _application = application;
    }

    [Fact]
    [Trait("Category", "SecurityExpectation")]
    public async Task CrossTeamUpdate_IsForbidden_AndForeignEventRemainsUnchanged()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var scenario = await TwoTeamSecurityScenarioBuilder.CreateAsync(
            _application.Services,
            cancellationToken);
        using var client = AuthenticatedTestClientFactory.Create(_application, scenario.UserA);
        var update = CreateUpdate(scenario.EventB, scenario.TeamA.Id, "Unauthorized update");

        using var response = await client.PutAsJsonAsync(
            $"/api/events?currentUserId={scenario.UserA.Id}&eventId={scenario.EventB.Id}",
            update,
            cancellationToken);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);

        await using var scope = _application.Services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var unchangedEvent = await dbContext.Events
            .AsNoTracking()
            .SingleAsync(value => value.Id == scenario.EventB.Id, cancellationToken);
        Assert.Equal(scenario.TeamB.Id, unchangedEvent.TeamId);
        Assert.Equal(scenario.EventB.Title, unchangedEvent.Title);
    }

    [Fact]
    [Trait("Category", "SecurityExpectation")]
    public async Task PrivateEventRead_ByUserFromAnotherTeam_IsForbidden()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var scenario = await TwoTeamSecurityScenarioBuilder.CreateAsync(
            _application.Services,
            cancellationToken);
        using var client = AuthenticatedTestClientFactory.Create(_application, scenario.UserA);

        using var response = await client.GetAsync($"/api/events/{scenario.EventB.Id}", cancellationToken);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact(Skip = "Activate in M2 after authorization fix")]
    [Trait("Category", "SecurityExpectation")]
    public async Task PrivateRosterRead_ByUserFromAnotherTeam_IsForbidden()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var scenario = await TwoTeamSecurityScenarioBuilder.CreateAsync(
            _application.Services,
            cancellationToken);
        using var client = AuthenticatedTestClientFactory.Create(_application, scenario.UserA);

        using var response = await client.GetAsync($"/api/lines?eventId={scenario.EventB.Id}", cancellationToken);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    private static UpdateEventDto CreateUpdate(ScheduledEvent source, Guid teamId, string title) =>
        new()
        {
            Title = title,
            Description = source.Description,
            Type = source.Type,
            StartTime = source.StartTime,
            DurationMinutes = source.DurationMinutes,
            Status = source.Status,
            LocationName = source.LocationName,
            LocationAddress = source.LocationAddress,
            IceRinkNumber = source.IceRinkNumber,
            TeamId = teamId,
        };
}
