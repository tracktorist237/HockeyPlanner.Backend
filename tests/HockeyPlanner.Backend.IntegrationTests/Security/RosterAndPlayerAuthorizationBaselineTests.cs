using System.Net;
using System.Net.Http.Json;
using HockeyPlanner.Backend.Core.Entities;
using HockeyPlanner.Backend.Core.Enums;
using HockeyPlanner.Backend.IntegrationTests.Fixtures;
using HockeyPlanner.Backend.IntegrationTests.Infrastructure;
using HockeyPlanner.Backend.Infrastructure.Data;
using HockeyPlanner.Backend.Shared.Models.Events;
using HockeyPlanner.Backend.Shared.Models.Lines;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace HockeyPlanner.Backend.IntegrationTests.Security;

[Collection(IntegrationTestCollection.Name)]
public sealed class RosterAndPlayerAuthorizationBaselineTests
{
    private readonly HockeyPlannerWebApplicationFactory _application;

    public RosterAndPlayerAuthorizationBaselineTests(HockeyPlannerWebApplicationFactory application)
    {
        _application = application;
    }

    [Fact]
    public async Task Anonymous_CanReadPublicRoster()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var scenario = await TwoTeamSecurityScenarioBuilder.CreateAsync(
            _application.Services,
            cancellationToken);
        await SetTeamVisibility(scenario.TeamB.Id, TeamVisibility.Public, cancellationToken);
        using var client = _application.CreateClient();

        using var response = await client.GetAsync(
            $"/api/lines?eventId={scenario.EventB.Id}",
            cancellationToken);
        var roster = await response.Content.ReadFromJsonAsync<List<LineDto>>(cancellationToken);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.NotNull(roster);
        var line = Assert.Single(roster);
        Assert.Equal(scenario.LineB.Id, line.Id);
        Assert.Equal(scenario.PlayerB.Id, Assert.Single(line.Members).PlayerId);
    }

    [Fact]
    public async Task Anonymous_PrivateRoster_ReturnsUnauthorized()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var scenario = await TwoTeamSecurityScenarioBuilder.CreateAsync(
            _application.Services,
            cancellationToken);
        using var client = _application.CreateClient();

        using var response = await client.GetAsync(
            $"/api/lines?eventId={scenario.EventB.Id}",
            cancellationToken);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Member_CanReadPrivateRoster()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var scenario = await TwoTeamSecurityScenarioBuilder.CreateAsync(
            _application.Services,
            cancellationToken);
        await SetMembershipRole(
            scenario.TeamB.Id,
            scenario.UserB.Id,
            TeamMemberRole.Member,
            cancellationToken);
        using var client = AuthenticatedTestClientFactory.Create(_application, scenario.UserB);

        using var response = await client.GetAsync(
            $"/api/lines?eventId={scenario.EventB.Id}",
            cancellationToken);
        var roster = await response.Content.ReadFromJsonAsync<List<LineDto>>(cancellationToken);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.NotNull(roster);
        Assert.Equal(scenario.LineB.Id, Assert.Single(roster).Id);
    }

    [Fact]
    public async Task EligibleGoalieOutsider_CannotReadPrivateRoster()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var scenario = await TwoTeamSecurityScenarioBuilder.CreateAsync(
            _application.Services,
            cancellationToken);
        await SetPrimaryPosition(scenario.UserA.Id, Position.Goalie, cancellationToken);
        await AddGoalieRequest(
            scenario,
            GoalieRequestVisibility.AllGoalies,
            GoalieRequestStatus.Open,
            cancellationToken: cancellationToken);
        using var client = AuthenticatedTestClientFactory.Create(_application, scenario.UserA);

        using var response = await client.GetAsync(
            $"/api/lines?eventId={scenario.EventB.Id}",
            cancellationToken);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task GoalieWithOwnApplication_CannotReadPrivateRoster()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var scenario = await TwoTeamSecurityScenarioBuilder.CreateAsync(
            _application.Services,
            cancellationToken);
        await SetPrimaryPosition(scenario.UserA.Id, Position.Goalie, cancellationToken);
        await AddGoalieRequest(
            scenario,
            GoalieRequestVisibility.TeamGoaliesOnly,
            GoalieRequestStatus.Closed,
            GoalieApplicationStatus.Pending,
            cancellationToken);
        using var client = AuthenticatedTestClientFactory.Create(_application, scenario.UserA);

        using var response = await client.GetAsync(
            $"/api/lines?eventId={scenario.EventB.Id}",
            cancellationToken);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task OrphanRoster_ReturnsNotFound()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var orphanEvent = await AddEvent(null, "Orphan roster", cancellationToken);
        using var client = _application.CreateClient();

        using var response = await client.GetAsync(
            $"/api/lines?eventId={orphanEvent.Id}",
            cancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task MissingEventRoster_ReturnsNotFound()
    {
        using var client = _application.CreateClient();

        using var response = await client.GetAsync(
            $"/api/lines?eventId={Guid.NewGuid()}",
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task SpoofedCurrentUserId_DoesNotGrantRosterMutationPermission()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var scenario = await TwoTeamSecurityScenarioBuilder.CreateAsync(
            _application.Services,
            cancellationToken);
        using var client = AuthenticatedTestClientFactory.Create(_application, scenario.UserA);
        var request = new CreateUpdateRosterRequest
        {
            EventId = scenario.EventB.Id,
            Lines = [],
        };

        using var response = await client.PutAsJsonAsync(
            $"/api/lines?currentUserId={scenario.UserB.Id}",
            request,
            cancellationToken);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        await AssertPlayerExists(scenario.PlayerB.Id, cancellationToken);
    }

    [Fact]
    public async Task Owner_CanDeletePlayerFromOwnEvent()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var scenario = await TwoTeamSecurityScenarioBuilder.CreateAsync(
            _application.Services,
            cancellationToken);
        using var client = AuthenticatedTestClientFactory.Create(_application, scenario.UserB);

        using var response = await client.DeleteAsync(
            $"/api/players?playerId={scenario.PlayerB.Id}",
            cancellationToken);
        var deleted = await response.Content.ReadFromJsonAsync<bool>(cancellationToken);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.True(deleted);
        await AssertPlayerMissing(scenario.PlayerB.Id, cancellationToken);
    }

    [Fact]
    public async Task Admin_CanDeletePlayerFromOwnEvent()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var scenario = await TwoTeamSecurityScenarioBuilder.CreateAsync(
            _application.Services,
            cancellationToken);
        await SetMembershipRole(
            scenario.TeamB.Id,
            scenario.UserB.Id,
            TeamMemberRole.Admin,
            cancellationToken);
        using var client = AuthenticatedTestClientFactory.Create(_application, scenario.UserB);

        using var response = await client.DeleteAsync(
            $"/api/players?playerId={scenario.PlayerB.Id}",
            cancellationToken);
        var deleted = await response.Content.ReadFromJsonAsync<bool>(cancellationToken);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.True(deleted);
        await AssertPlayerMissing(scenario.PlayerB.Id, cancellationToken);
    }

    [Fact]
    public async Task Member_CannotDeletePlayer_AndPlayerRemains()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var scenario = await TwoTeamSecurityScenarioBuilder.CreateAsync(
            _application.Services,
            cancellationToken);
        await SetMembershipRole(
            scenario.TeamB.Id,
            scenario.UserB.Id,
            TeamMemberRole.Member,
            cancellationToken);
        using var client = AuthenticatedTestClientFactory.Create(_application, scenario.UserB);

        using var response = await client.DeleteAsync(
            $"/api/players?playerId={scenario.PlayerB.Id}",
            cancellationToken);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        await AssertPlayerExists(scenario.PlayerB.Id, cancellationToken);
    }

    [Fact]
    public async Task EligibleGoalieOutsider_CannotDeletePlayer_AndPlayerRemains()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var scenario = await TwoTeamSecurityScenarioBuilder.CreateAsync(
            _application.Services,
            cancellationToken);
        await SetPrimaryPosition(scenario.UserA.Id, Position.Goalie, cancellationToken);
        await AddGoalieRequest(
            scenario,
            GoalieRequestVisibility.AllGoalies,
            GoalieRequestStatus.Open,
            cancellationToken: cancellationToken);
        using var client = AuthenticatedTestClientFactory.Create(_application, scenario.UserA);

        using var response = await client.DeleteAsync(
            $"/api/players?playerId={scenario.PlayerB.Id}",
            cancellationToken);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        await AssertPlayerExists(scenario.PlayerB.Id, cancellationToken);
    }

    [Fact]
    public async Task SuperAdminWithoutMembership_CannotDeletePlayer_AndPlayerRemains()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var scenario = await TwoTeamSecurityScenarioBuilder.CreateAsync(
            _application.Services,
            cancellationToken);
        scenario.UserA.AppRole = AppRole.SuperAdmin;
        using var client = AuthenticatedTestClientFactory.Create(_application, scenario.UserA);

        using var response = await client.DeleteAsync(
            $"/api/players?playerId={scenario.PlayerB.Id}",
            cancellationToken);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        await AssertPlayerExists(scenario.PlayerB.Id, cancellationToken);
    }

    [Fact]
    public async Task Anonymous_CannotDeletePlayer_AndPlayerRemains()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var scenario = await TwoTeamSecurityScenarioBuilder.CreateAsync(
            _application.Services,
            cancellationToken);
        using var client = _application.CreateClient();

        using var response = await client.DeleteAsync(
            $"/api/players?playerId={scenario.PlayerB.Id}",
            cancellationToken);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        await AssertPlayerExists(scenario.PlayerB.Id, cancellationToken);
    }

    [Fact]
    public async Task MissingPlayerDelete_ReturnsNotFound()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var scenario = await TwoTeamSecurityScenarioBuilder.CreateAsync(
            _application.Services,
            cancellationToken);
        using var client = AuthenticatedTestClientFactory.Create(_application, scenario.UserB);

        using var response = await client.DeleteAsync(
            $"/api/players?playerId={Guid.NewGuid()}",
            cancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task OrphanEventPlayerDelete_ReturnsForbidden_AndPlayerRemains()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var scenario = await TwoTeamSecurityScenarioBuilder.CreateAsync(
            _application.Services,
            cancellationToken);
        var orphanPlayer = await AddOrphanPlayer(scenario.UserB, cancellationToken);
        using var client = AuthenticatedTestClientFactory.Create(_application, scenario.UserB);

        using var response = await client.DeleteAsync(
            $"/api/players?playerId={orphanPlayer.Id}",
            cancellationToken);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        await AssertPlayerExists(orphanPlayer.Id, cancellationToken);
    }

    private async Task SetMembershipRole(
        Guid teamId,
        Guid userId,
        TeamMemberRole role,
        CancellationToken cancellationToken)
    {
        await using var scope = _application.Services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        await dbContext.TeamMemberships
            .Where(value => value.TeamId == teamId && value.UserId == userId)
            .ExecuteUpdateAsync(
                setters => setters.SetProperty(value => value.Role, role),
                cancellationToken);
    }

    private async Task SetTeamVisibility(
        Guid teamId,
        TeamVisibility visibility,
        CancellationToken cancellationToken)
    {
        await using var scope = _application.Services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        await dbContext.Teams
            .Where(value => value.Id == teamId)
            .ExecuteUpdateAsync(
                setters => setters.SetProperty(value => value.Visibility, visibility),
                cancellationToken);
    }

    private async Task SetPrimaryPosition(
        Guid userId,
        Position position,
        CancellationToken cancellationToken)
    {
        await using var scope = _application.Services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        await dbContext.Users
            .Where(value => value.Id == userId)
            .ExecuteUpdateAsync(
                setters => setters.SetProperty(value => value.PrimaryPosition, position),
                cancellationToken);
    }

    private async Task AddGoalieRequest(
        TwoTeamSecurityScenario scenario,
        GoalieRequestVisibility visibility,
        GoalieRequestStatus status,
        GoalieApplicationStatus? applicationStatus = null,
        CancellationToken cancellationToken = default)
    {
        var request = new GoalieRequest
        {
            EventId = scenario.EventB.Id,
            TeamId = scenario.TeamB.Id,
            CreatedByUserId = scenario.UserB.Id,
            NeededCount = 1,
            Visibility = visibility,
            ResponseMode = GoalieRequestResponseMode.Manual,
            Status = status,
        };

        if (applicationStatus.HasValue)
        {
            request.Applications.Add(new GoalieApplication
            {
                GoalieUserId = scenario.UserA.Id,
                Status = applicationStatus.Value,
                Source = GoalieApplicationSource.Application,
            });
        }

        await using var scope = _application.Services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        await dbContext.GoalieRequests.AddAsync(request, cancellationToken);
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    private async Task<ScheduledEvent> AddEvent(
        Guid? teamId,
        string title,
        CancellationToken cancellationToken)
    {
        var scheduledEvent = new ScheduledEvent
        {
            Title = title,
            Type = EventType.Practice,
            StartTime = DateTime.UtcNow.AddDays(2),
            DurationMinutes = 75,
            Status = EventStatus.Scheduled,
            LocationName = "Test rink",
            LocationAddress = "Test address",
            TeamId = teamId,
        };

        await using var scope = _application.Services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        await dbContext.Events.AddAsync(scheduledEvent, cancellationToken);
        await dbContext.SaveChangesAsync(cancellationToken);
        return scheduledEvent;
    }

    private async Task<Player> AddOrphanPlayer(User user, CancellationToken cancellationToken)
    {
        var orphanEvent = new ScheduledEvent
        {
            Title = "Orphan player event",
            Type = EventType.Practice,
            StartTime = DateTime.UtcNow.AddDays(2),
            DurationMinutes = 75,
            Status = EventStatus.Scheduled,
            LocationName = "Test rink",
            LocationAddress = "Test address",
            TeamId = null,
        };
        var line = new Line
        {
            Name = "Orphan line",
            Order = 1,
            EventId = orphanEvent.Id,
        };
        var player = new Player
        {
            LineId = line.Id,
            UserId = user.Id,
            FirstName = user.FirstName,
            LastName = user.LastName,
            Role = PlayerRole.Center,
        };

        await using var scope = _application.Services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        await dbContext.AddRangeAsync(
            new object[] { orphanEvent, line, player },
            cancellationToken);
        await dbContext.SaveChangesAsync(cancellationToken);
        return player;
    }

    private async Task AssertPlayerExists(Guid playerId, CancellationToken cancellationToken)
    {
        await using var scope = _application.Services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.True(await dbContext.Players
            .AsNoTracking()
            .AnyAsync(value => value.Id == playerId, cancellationToken));
    }

    private async Task AssertPlayerMissing(Guid playerId, CancellationToken cancellationToken)
    {
        await using var scope = _application.Services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.False(await dbContext.Players
            .AsNoTracking()
            .AnyAsync(value => value.Id == playerId, cancellationToken));
    }
}
