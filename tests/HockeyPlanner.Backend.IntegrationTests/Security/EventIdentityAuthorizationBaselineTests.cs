using System.Net;
using System.Net.Http.Json;
using HockeyPlanner.Backend.Core.Entities;
using HockeyPlanner.Backend.Core.Enums;
using HockeyPlanner.Backend.IntegrationTests.Fixtures;
using HockeyPlanner.Backend.IntegrationTests.Infrastructure;
using HockeyPlanner.Backend.Infrastructure.Data;
using HockeyPlanner.Backend.Shared.Models.Events;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace HockeyPlanner.Backend.IntegrationTests.Security;

[Collection(IntegrationTestCollection.Name)]
[Trait("Category", "EventIdentityAuthorization")]
public sealed class EventIdentityAuthorizationBaselineTests
{
    private readonly HockeyPlannerWebApplicationFactory _application;

    public EventIdentityAuthorizationBaselineTests(HockeyPlannerWebApplicationFactory application)
    {
        _application = application;
    }

    [Fact]
    public async Task Owner_CanUpdateOwnEvent_WhenTeamDoesNotChange()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var scenario = await TwoTeamSecurityScenarioBuilder.CreateAsync(
            _application.Services,
            cancellationToken);
        using var client = AuthenticatedTestClientFactory.Create(_application, scenario.UserB);
        var update = CreateUpdate(scenario.EventB, scenario.TeamB.Id, "Owner update");

        using var response = await client.PutAsJsonAsync(
            $"/api/events?currentUserId={scenario.UserA.Id}&eventId={scenario.EventB.Id}",
            update,
            cancellationToken);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        await AssertEventState(scenario.EventB.Id, scenario.TeamB.Id, update.Title, cancellationToken);
    }

    [Fact]
    public async Task Member_CanReadPrivateEvent()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var scenario = await TwoTeamSecurityScenarioBuilder.CreateAsync(
            _application.Services,
            cancellationToken);
        await SetMembershipRole(scenario.TeamB.Id, scenario.UserB.Id, TeamMemberRole.Member, cancellationToken);
        using var client = AuthenticatedTestClientFactory.Create(_application, scenario.UserB);

        using var response = await client.GetAsync($"/api/events/{scenario.EventB.Id}", cancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Member_CanUpdateOwnAttendance()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var scenario = await TwoTeamSecurityScenarioBuilder.CreateAsync(
            _application.Services,
            cancellationToken);
        await SetMembershipRole(scenario.TeamB.Id, scenario.UserB.Id, TeamMemberRole.Member, cancellationToken);
        using var client = AuthenticatedTestClientFactory.Create(_application, scenario.UserB);
        var request = new UpdateAttendanceRequest
        {
            Status = AttendanceStatus.Confirmed,
            Notes = "Member response",
        };

        using var response = await client.PostAsJsonAsync(
            $"/api/events/{scenario.EventB.Id}/attendance/{scenario.UserB.Id}" +
            $"?currentUserId={scenario.UserA.Id}",
            request,
            cancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        await using var scope = _application.Services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var attendance = await dbContext.Attendances
            .AsNoTracking()
            .SingleAsync(value => value.Id == scenario.AttendanceB.Id, cancellationToken);
        Assert.Equal(AttendanceStatus.Confirmed, attendance.Status);
        Assert.Equal(request.Notes, attendance.Notes);
    }

    [Fact]
    public async Task Anonymous_CanReadPublicEvent()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var scenario = await TwoTeamSecurityScenarioBuilder.CreateAsync(
            _application.Services,
            cancellationToken);
        await SetTeamVisibility(scenario.TeamB.Id, TeamVisibility.Public, cancellationToken);
        using var client = _application.CreateClient();

        using var response = await client.GetAsync($"/api/events/{scenario.EventB.Id}", cancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Anonymous_PrivateEventRead_ReturnsUnauthorized()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var scenario = await TwoTeamSecurityScenarioBuilder.CreateAsync(
            _application.Services,
            cancellationToken);
        using var client = _application.CreateClient();

        using var response = await client.GetAsync($"/api/events/{scenario.EventB.Id}", cancellationToken);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task AnonymousList_ContainsPublicEvent_AndExcludesPrivateEvent()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var scenario = await TwoTeamSecurityScenarioBuilder.CreateAsync(
            _application.Services,
            cancellationToken);
        await SetTeamVisibility(scenario.TeamA.Id, TeamVisibility.Public, cancellationToken);
        var publicEvent = await AddEvent(scenario.TeamA.Id, "Public event", cancellationToken);
        using var client = _application.CreateClient();

        using var response = await client.GetAsync("/api/events", cancellationToken);
        var body = await response.Content.ReadFromJsonAsync<EventListDto>(cancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.NotNull(body?.Events);
        Assert.Contains(body.Events, value => value.Id == publicEvent.Id);
        Assert.DoesNotContain(body.Events, value => value.Id == scenario.EventB.Id);
    }

    [Fact]
    public async Task AuthenticatedOutsiderList_DoesNotContainPrivateEvent()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var scenario = await TwoTeamSecurityScenarioBuilder.CreateAsync(
            _application.Services,
            cancellationToken);
        using var client = AuthenticatedTestClientFactory.Create(_application, scenario.UserA);

        using var response = await client.GetAsync(
            $"/api/events?currentUserId={scenario.UserB.Id}",
            cancellationToken);
        var body = await response.Content.ReadFromJsonAsync<EventListDto>(cancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.NotNull(body?.Events);
        Assert.DoesNotContain(body.Events, value => value.Id == scenario.EventB.Id);
    }

    [Fact]
    public async Task Anonymous_PublicTeamList_ReturnsTeamEvents()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var scenario = await TwoTeamSecurityScenarioBuilder.CreateAsync(
            _application.Services,
            cancellationToken);
        await SetTeamVisibility(scenario.TeamB.Id, TeamVisibility.Public, cancellationToken);
        using var client = _application.CreateClient();

        using var response = await client.GetAsync(
            $"/api/events?teamId={scenario.TeamB.Id}",
            cancellationToken);
        var body = await response.Content.ReadFromJsonAsync<EventListDto>(cancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.NotNull(body?.Events);
        Assert.Contains(body.Events, value => value.Id == scenario.EventB.Id);
    }

    [Fact]
    public async Task Anonymous_PrivateTeamList_ReturnsUnauthorized()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var scenario = await TwoTeamSecurityScenarioBuilder.CreateAsync(
            _application.Services,
            cancellationToken);
        using var client = _application.CreateClient();

        using var response = await client.GetAsync(
            $"/api/events?teamId={scenario.TeamB.Id}",
            cancellationToken);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Member_PrivateTeamList_ReturnsTeamEvents()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var scenario = await TwoTeamSecurityScenarioBuilder.CreateAsync(
            _application.Services,
            cancellationToken);
        await SetMembershipRole(scenario.TeamB.Id, scenario.UserB.Id, TeamMemberRole.Member, cancellationToken);
        using var client = AuthenticatedTestClientFactory.Create(_application, scenario.UserB);

        using var response = await client.GetAsync(
            $"/api/events?currentUserId={scenario.UserA.Id}&teamId={scenario.TeamB.Id}",
            cancellationToken);
        var body = await response.Content.ReadFromJsonAsync<EventListDto>(cancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.NotNull(body?.Events);
        Assert.Contains(body.Events, value => value.Id == scenario.EventB.Id);
    }

    [Fact]
    public async Task AuthenticatedOutsider_PrivateTeamList_ReturnsForbidden()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var scenario = await TwoTeamSecurityScenarioBuilder.CreateAsync(
            _application.Services,
            cancellationToken);
        using var client = AuthenticatedTestClientFactory.Create(_application, scenario.UserA);

        using var response = await client.GetAsync(
            $"/api/events?currentUserId={scenario.UserB.Id}&teamId={scenario.TeamB.Id}",
            cancellationToken);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task AuthenticatedList_ContainsOwnTeamEvent_AndExcludesForeignPublicEvent()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var scenario = await TwoTeamSecurityScenarioBuilder.CreateAsync(
            _application.Services,
            cancellationToken);
        var ownPrivateEvent = await AddEvent(scenario.TeamA.Id, "Own private event", cancellationToken);
        await SetTeamVisibility(scenario.TeamB.Id, TeamVisibility.Public, cancellationToken);
        using var client = AuthenticatedTestClientFactory.Create(_application, scenario.UserA);

        using var response = await client.GetAsync("/api/events", cancellationToken);
        var body = await response.Content.ReadFromJsonAsync<EventListDto>(cancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.NotNull(body?.Events);
        Assert.Contains(body.Events, value => value.Id == ownPrivateEvent.Id);
        Assert.DoesNotContain(body.Events, value => value.Id == scenario.EventB.Id);
    }

    [Fact]
    public async Task AuthenticatedOutsider_PublicTeamList_ReturnsTeamEvents()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var scenario = await TwoTeamSecurityScenarioBuilder.CreateAsync(
            _application.Services,
            cancellationToken);
        await SetTeamVisibility(scenario.TeamB.Id, TeamVisibility.Public, cancellationToken);
        using var client = AuthenticatedTestClientFactory.Create(_application, scenario.UserA);

        using var response = await client.GetAsync(
            $"/api/events?teamId={scenario.TeamB.Id}",
            cancellationToken);
        var body = await response.Content.ReadFromJsonAsync<EventListDto>(cancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.NotNull(body?.Events);
        Assert.Contains(body.Events, value => value.Id == scenario.EventB.Id);
    }

    [Fact]
    public async Task Owner_CannotMoveOwnEventToAnotherTeam_AndEventRemainsUnchanged()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var scenario = await TwoTeamSecurityScenarioBuilder.CreateAsync(
            _application.Services,
            cancellationToken);
        using var client = AuthenticatedTestClientFactory.Create(_application, scenario.UserB);
        var update = CreateUpdate(scenario.EventB, scenario.TeamA.Id, "Move attempt");

        using var response = await client.PutAsJsonAsync(
            $"/api/events?currentUserId={scenario.UserB.Id}&eventId={scenario.EventB.Id}",
            update,
            cancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        await AssertEventState(
            scenario.EventB.Id,
            scenario.TeamB.Id,
            scenario.EventB.Title,
            cancellationToken);
    }

    [Fact]
    public async Task AnonymousAttendanceMutation_ReturnsUnauthorized_AndAttendanceRemainsUnchanged()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var scenario = await TwoTeamSecurityScenarioBuilder.CreateAsync(
            _application.Services,
            cancellationToken);
        using var client = _application.CreateClient();
        var request = new UpdateAttendanceRequest
        {
            Status = AttendanceStatus.Declined,
            Notes = "Anonymous mutation",
        };

        using var response = await client.PostAsJsonAsync(
            $"/api/events/{scenario.EventB.Id}/attendance/{scenario.UserB.Id}" +
            $"?currentUserId={scenario.UserB.Id}",
            request,
            cancellationToken);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        await AssertAttendanceUnchanged(scenario.AttendanceB.Id, cancellationToken);
    }

    [Fact]
    public async Task AnonymousList_DoesNotContainOrphanEvent()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var orphanEvent = await AddEvent(null, "Anonymous orphan", cancellationToken);
        using var client = _application.CreateClient();

        using var response = await client.GetAsync("/api/events", cancellationToken);
        var body = await response.Content.ReadFromJsonAsync<EventListDto>(cancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.NotNull(body?.Events);
        Assert.DoesNotContain(body.Events, value => value.Id == orphanEvent.Id);
    }

    [Fact]
    public async Task AuthenticatedNormalUserList_DoesNotContainOrphanEvent()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var scenario = await TwoTeamSecurityScenarioBuilder.CreateAsync(
            _application.Services,
            cancellationToken);
        var orphanEvent = await AddEvent(null, "Authenticated orphan", cancellationToken);
        using var client = AuthenticatedTestClientFactory.Create(_application, scenario.UserA);

        using var response = await client.GetAsync(
            $"/api/events?currentUserId={scenario.UserB.Id}",
            cancellationToken);
        var body = await response.Content.ReadFromJsonAsync<EventListDto>(cancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.NotNull(body?.Events);
        Assert.DoesNotContain(body.Events, value => value.Id == orphanEvent.Id);
    }

    [Fact]
    public async Task DirectGet_OrphanEvent_ReturnsNotFound()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var scenario = await TwoTeamSecurityScenarioBuilder.CreateAsync(
            _application.Services,
            cancellationToken);
        var orphanEvent = await AddEvent(null, "Direct orphan", cancellationToken);
        using var client = AuthenticatedTestClientFactory.Create(_application, scenario.UserA);

        using var response = await client.GetAsync($"/api/events/{orphanEvent.Id}", cancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task AuthenticatedMutation_OrphanEvent_ReturnsForbidden_AndEventRemainsUnchanged()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var scenario = await TwoTeamSecurityScenarioBuilder.CreateAsync(
            _application.Services,
            cancellationToken);
        var orphanEvent = await AddEvent(null, "Protected orphan", cancellationToken);
        using var client = AuthenticatedTestClientFactory.Create(_application, scenario.UserA);
        var update = CreateUpdate(orphanEvent, scenario.TeamA.Id, "Orphan mutation");

        using var response = await client.PutAsJsonAsync(
            $"/api/events?currentUserId={scenario.UserA.Id}&eventId={orphanEvent.Id}",
            update,
            cancellationToken);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        await AssertEventState(orphanEvent.Id, null, orphanEvent.Title, cancellationToken);
    }

    [Fact]
    public async Task AnonymousMutation_OrphanEvent_ReturnsUnauthorized_AndEventRemainsUnchanged()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var orphanEvent = await AddEvent(null, "Anonymous orphan mutation", cancellationToken);
        using var client = _application.CreateClient();
        var update = CreateUpdate(orphanEvent, null, "Anonymous mutation");

        using var response = await client.PutAsJsonAsync(
            $"/api/events?currentUserId={Guid.NewGuid()}&eventId={orphanEvent.Id}",
            update,
            cancellationToken);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        await AssertEventState(orphanEvent.Id, null, orphanEvent.Title, cancellationToken);
    }

    [Fact]
    public async Task GoalieOutsider_OpenAllGoaliesRequest_AppearsInList()
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

        using var response = await client.GetAsync("/api/events", cancellationToken);
        var body = await response.Content.ReadFromJsonAsync<EventListDto>(cancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.NotNull(body?.Events);
        Assert.Contains(body.Events, value => value.Id == scenario.EventB.Id);
    }

    [Fact]
    public async Task OrdinaryOutsider_OpenAllGoaliesRequest_DoesNotAppearInList()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var scenario = await TwoTeamSecurityScenarioBuilder.CreateAsync(
            _application.Services,
            cancellationToken);
        await AddGoalieRequest(
            scenario,
            GoalieRequestVisibility.AllGoalies,
            GoalieRequestStatus.Open,
            cancellationToken: cancellationToken);
        using var client = AuthenticatedTestClientFactory.Create(_application, scenario.UserA);

        using var response = await client.GetAsync("/api/events", cancellationToken);
        var body = await response.Content.ReadFromJsonAsync<EventListDto>(cancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.NotNull(body?.Events);
        Assert.DoesNotContain(body.Events, value => value.Id == scenario.EventB.Id);
    }

    [Fact]
    public async Task GoalieOutsider_ClosedAllGoaliesRequest_DoesNotAppearInList()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var scenario = await TwoTeamSecurityScenarioBuilder.CreateAsync(
            _application.Services,
            cancellationToken);
        await SetPrimaryPosition(scenario.UserA.Id, Position.Goalie, cancellationToken);
        await AddGoalieRequest(
            scenario,
            GoalieRequestVisibility.AllGoalies,
            GoalieRequestStatus.Closed,
            cancellationToken: cancellationToken);
        using var client = AuthenticatedTestClientFactory.Create(_application, scenario.UserA);

        using var response = await client.GetAsync("/api/events", cancellationToken);
        var body = await response.Content.ReadFromJsonAsync<EventListDto>(cancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.NotNull(body?.Events);
        Assert.DoesNotContain(body.Events, value => value.Id == scenario.EventB.Id);
    }

    [Fact]
    public async Task GoalieOutsider_ClosedAllGoaliesRequest_CannotReadEvent()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var scenario = await TwoTeamSecurityScenarioBuilder.CreateAsync(
            _application.Services,
            cancellationToken);
        await SetPrimaryPosition(scenario.UserA.Id, Position.Goalie, cancellationToken);
        await AddGoalieRequest(
            scenario,
            GoalieRequestVisibility.AllGoalies,
            GoalieRequestStatus.Closed,
            cancellationToken: cancellationToken);
        using var client = AuthenticatedTestClientFactory.Create(_application, scenario.UserA);

        using var response = await client.GetAsync($"/api/events/{scenario.EventB.Id}", cancellationToken);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task GoalieOutsider_OpenAllGoaliesRequest_CanReadSanitizedEvent()
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
        await AssertSensitiveEventGraphExists(scenario, cancellationToken);
        using var client = AuthenticatedTestClientFactory.Create(_application, scenario.UserA);

        using var response = await client.GetAsync($"/api/events/{scenario.EventB.Id}", cancellationToken);
        var body = await response.Content.ReadFromJsonAsync<EventDto>(cancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.NotNull(body);
        Assert.Equal(scenario.EventB.Id, body.Id);
        Assert.Equal(scenario.EventB.Title, body.Title);
        Assert.Equal(scenario.TeamB.Id, body.TeamId);
        Assert.Equal(scenario.TeamB.Name, body.TeamName);
        Assert.Empty(body.Attendances);
        Assert.Empty(body.Roster);
        Assert.Empty(body.Exercises);
        Assert.Null(body.UniformColorId);
        Assert.Null(body.UniformColor);
    }

    [Fact]
    public async Task OrdinaryOutsider_OpenAllGoaliesRequest_CannotReadEvent()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var scenario = await TwoTeamSecurityScenarioBuilder.CreateAsync(
            _application.Services,
            cancellationToken);
        await AddGoalieRequest(
            scenario,
            GoalieRequestVisibility.AllGoalies,
            GoalieRequestStatus.Open,
            cancellationToken: cancellationToken);
        using var client = AuthenticatedTestClientFactory.Create(_application, scenario.UserA);

        using var response = await client.GetAsync($"/api/events/{scenario.EventB.Id}", cancellationToken);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Theory]
    [InlineData(GoalieApplicationStatus.Pending)]
    [InlineData(GoalieApplicationStatus.Accepted)]
    [InlineData(GoalieApplicationStatus.Rejected)]
    [InlineData(GoalieApplicationStatus.Proposed)]
    [InlineData(GoalieApplicationStatus.Confirmed)]
    [InlineData(GoalieApplicationStatus.Declined)]
    [InlineData(GoalieApplicationStatus.Cancelled)]
    public async Task GoalieOutsider_ExistingApplication_CanReadSanitizedEvent(
        GoalieApplicationStatus applicationStatus)
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
            applicationStatus,
            cancellationToken);
        using var client = AuthenticatedTestClientFactory.Create(_application, scenario.UserA);

        using var response = await client.GetAsync($"/api/events/{scenario.EventB.Id}", cancellationToken);
        var body = await response.Content.ReadFromJsonAsync<EventDto>(cancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.NotNull(body);
        Assert.Equal(scenario.EventB.Id, body.Id);
        Assert.Empty(body.Attendances);
        Assert.Empty(body.Roster);
        Assert.Empty(body.Exercises);
    }

    [Fact]
    public async Task GoalieOutsider_ReadAccess_DoesNotGrantEventMutation()
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
        var update = CreateUpdate(scenario.EventB, scenario.TeamB.Id, "Goalie mutation");

        using var response = await client.PutAsJsonAsync(
            $"/api/events?currentUserId={scenario.UserB.Id}&eventId={scenario.EventB.Id}",
            update,
            cancellationToken);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        await AssertEventState(
            scenario.EventB.Id,
            scenario.TeamB.Id,
            scenario.EventB.Title,
            cancellationToken);
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
            .ExecuteUpdateAsync(setters => setters.SetProperty(value => value.Role, role), cancellationToken);
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
            .ExecuteUpdateAsync(setters => setters.SetProperty(value => value.Visibility, visibility), cancellationToken);
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

    private async Task<GoalieRequest> AddGoalieRequest(
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
                Source = applicationStatus == GoalieApplicationStatus.Proposed
                    ? GoalieApplicationSource.ManualProposal
                    : GoalieApplicationSource.Application,
            });
        }

        await using var scope = _application.Services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        await dbContext.GoalieRequests.AddAsync(request, cancellationToken);
        await dbContext.SaveChangesAsync(cancellationToken);
        dbContext.ChangeTracker.Clear();
        return request;
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
        dbContext.ChangeTracker.Clear();
        return scheduledEvent;
    }

    private async Task AssertEventState(
        Guid eventId,
        Guid? expectedTeamId,
        string expectedTitle,
        CancellationToken cancellationToken)
    {
        await using var scope = _application.Services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var scheduledEvent = await dbContext.Events
            .AsNoTracking()
            .SingleAsync(value => value.Id == eventId, cancellationToken);
        Assert.Equal(expectedTeamId, scheduledEvent.TeamId);
        Assert.Equal(expectedTitle, scheduledEvent.Title);
    }

    private async Task AssertAttendanceUnchanged(Guid attendanceId, CancellationToken cancellationToken)
    {
        await using var scope = _application.Services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var attendance = await dbContext.Attendances
            .AsNoTracking()
            .SingleAsync(value => value.Id == attendanceId, cancellationToken);
        Assert.Equal(AttendanceStatus.Confirmed, attendance.Status);
        Assert.Null(attendance.Notes);
    }

    private async Task AssertSensitiveEventGraphExists(
        TwoTeamSecurityScenario scenario,
        CancellationToken cancellationToken)
    {
        await using var scope = _application.Services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.True(await dbContext.Attendances
            .AsNoTracking()
            .AnyAsync(
                value => value.Id == scenario.AttendanceB.Id && value.EventId == scenario.EventB.Id,
                cancellationToken));
        Assert.True(await dbContext.Players
            .AsNoTracking()
            .AnyAsync(
                value => value.Id == scenario.PlayerB.Id && value.Line.EventId == scenario.EventB.Id,
                cancellationToken));
    }

    private static UpdateEventDto CreateUpdate(ScheduledEvent source, Guid? teamId, string title) =>
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
