using System.Net;
using System.Net.Http.Json;
using HockeyPlanner.Backend.Core.Enums;
using HockeyPlanner.Backend.IntegrationTests.Fixtures;
using HockeyPlanner.Backend.IntegrationTests.Infrastructure;
using HockeyPlanner.Backend.Infrastructure.Data;
using HockeyPlanner.Backend.Shared.Models.Events;
using HockeyPlanner.Backend.WebAPI.Models.Auth;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace HockeyPlanner.Backend.IntegrationTests.Security;

[Collection(IntegrationTestCollection.Name)]
[Trait("Category", "AuthorizedOwnerBaseline")]
public sealed class AuthorizedOwnerBaselineTests
{
    private readonly HockeyPlannerWebApplicationFactory _application;

    public AuthorizedOwnerBaselineTests(HockeyPlannerWebApplicationFactory application)
    {
        _application = application;
    }

    [Fact]
    public async Task ScenarioBuilder_CreatesTwoIsolatedPrivateTeams()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var scenario = await TwoTeamSecurityScenarioBuilder.CreateAsync(
            _application.Services,
            cancellationToken);

        await using var scope = _application.Services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var scenarioTeams = await dbContext.Teams
            .AsNoTracking()
            .Where(team => team.Id == scenario.TeamA.Id || team.Id == scenario.TeamB.Id)
            .ToListAsync(cancellationToken);
        Assert.Equal(2, scenarioTeams.Count);
        Assert.All(scenarioTeams, team => Assert.Equal(TeamVisibility.Private, team.Visibility));
        Assert.Equal(TeamMemberRole.Owner, await dbContext.TeamMemberships
            .Where(membership => membership.TeamId == scenario.TeamA.Id &&
                                 membership.UserId == scenario.UserA.Id)
            .Select(membership => membership.Role)
            .SingleAsync(cancellationToken));
        Assert.Equal(TeamMemberRole.Owner, await dbContext.TeamMemberships
            .Where(membership => membership.TeamId == scenario.TeamB.Id &&
                                 membership.UserId == scenario.UserB.Id)
            .Select(membership => membership.Role)
            .SingleAsync(cancellationToken));
        Assert.False(await dbContext.TeamMemberships.AnyAsync(
            membership =>
                (membership.TeamId == scenario.TeamA.Id && membership.UserId == scenario.UserB.Id) ||
                (membership.TeamId == scenario.TeamB.Id && membership.UserId == scenario.UserA.Id),
            cancellationToken));
        Assert.Equal(scenario.TeamB.Id, await dbContext.Events
            .Where(value => value.Id == scenario.EventB.Id)
            .Select(value => value.TeamId)
            .SingleAsync(cancellationToken));
        var attendance = await dbContext.Attendances
            .AsNoTracking()
            .SingleAsync(value => value.Id == scenario.AttendanceB.Id, cancellationToken);
        Assert.Equal(scenario.EventB.Id, attendance.EventId);
        Assert.Equal(scenario.UserB.Id, attendance.UserId);
        Assert.Equal(scenario.EventB.Id, await dbContext.Lines
            .Where(value => value.Id == scenario.LineB.Id)
            .Select(value => value.EventId)
            .SingleAsync(cancellationToken));
        var player = await dbContext.Players
            .AsNoTracking()
            .SingleAsync(value => value.Id == scenario.PlayerB.Id, cancellationToken);
        Assert.Equal(scenario.LineB.Id, player.LineId);
        Assert.Equal(scenario.UserB.Id, player.UserId);
    }

    [Fact]
    public async Task RealJwt_AllowsOwnerToReadOwnIdentity()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var scenario = await TwoTeamSecurityScenarioBuilder.CreateAsync(
            _application.Services,
            cancellationToken);
        using var client = AuthenticatedTestClientFactory.Create(_application, scenario.UserB);

        using var response = await client.GetAsync("/api/auth/me", cancellationToken);
        var body = await response.Content.ReadFromJsonAsync<AuthUserResponse>(cancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.NotNull(body);
        Assert.Equal(scenario.UserB.Id, body.Id);
        Assert.Equal(scenario.UserB.Email, body.Email);
    }

    [Fact]
    public async Task Owner_CanReadOwnEvent_WithStableBasicContract()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var scenario = await TwoTeamSecurityScenarioBuilder.CreateAsync(
            _application.Services,
            cancellationToken);
        using var client = AuthenticatedTestClientFactory.Create(_application, scenario.UserB);

        using var response = await client.GetAsync($"/api/events/{scenario.EventB.Id}", cancellationToken);
        var body = await response.Content.ReadFromJsonAsync<EventDto>(cancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.NotNull(body);
        Assert.Equal(scenario.EventB.Id, body.Id);
        Assert.Equal(scenario.EventB.Title, body.Title);
        Assert.Equal(scenario.TeamB.Id, body.TeamId);
    }

    [Fact]
    public async Task Owner_CanReadOwnRoster_WithStableBasicContract()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var scenario = await TwoTeamSecurityScenarioBuilder.CreateAsync(
            _application.Services,
            cancellationToken);
        using var client = AuthenticatedTestClientFactory.Create(_application, scenario.UserB);

        using var response = await client.GetAsync($"/api/lines?eventId={scenario.EventB.Id}", cancellationToken);
        var body = await response.Content.ReadFromJsonAsync<List<LineDto>>(cancellationToken);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.NotNull(body);
        var line = Assert.Single(body);
        Assert.Equal(scenario.LineB.Id, line.Id);
        Assert.Equal(scenario.LineB.Name, line.Name);
        var player = Assert.Single(line.Members);
        Assert.Equal(scenario.UserB.Id, player.UserId);
    }
}
