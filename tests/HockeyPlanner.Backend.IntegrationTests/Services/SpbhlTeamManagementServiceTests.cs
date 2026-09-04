using HockeyPlanner.Backend.Core.Entities;
using HockeyPlanner.Backend.Core.Enums;
using HockeyPlanner.Backend.Core.Exceptions;
using HockeyPlanner.Backend.Infrastructure.Data;
using HockeyPlanner.Backend.IntegrationTests.Infrastructure;
using HockeyPlanner.Backend.WebAPI.Models.Spbhl;
using HockeyPlanner.Backend.WebAPI.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace HockeyPlanner.Backend.IntegrationTests.Services;

[Collection(IntegrationTestCollection.Name)]
[Trait("Category", "SpbhlTeamManagement")]
public sealed class SpbhlTeamManagementServiceTests(HockeyPlannerWebApplicationFactory factory)
{
    [Fact]
    public async Task MissingTeam_IsNotFound()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var scope = factory.Services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var service = CreateService(context, new FakeSpbhlClient(), new FakeSyncService());

        await Assert.ThrowsAsync<NotFoundException>(() =>
            service.GetStatusAsync(Guid.NewGuid(), Guid.NewGuid(), cancellationToken));
    }

    [Theory]
    [InlineData(TeamMemberRole.Member)]
    [InlineData(null)]
    public async Task NonManager_CannotUseManagementOperations(TeamMemberRole? role)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var scope = factory.Services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var scenario = await SeedAsync(context, role, cancellationToken);
        var client = new FakeSpbhlClient();
        var sync = new FakeSyncService();
        var service = CreateService(context, client, sync);

        await Assert.ThrowsAsync<UnauthorizedException>(() => service.GetStatusAsync(scenario.Team.Id, scenario.Actor.Id, cancellationToken));
        await Assert.ThrowsAsync<UnauthorizedException>(() => service.SearchTeamsAsync(scenario.Team.Id, scenario.Actor.Id, "Ладога", cancellationToken));
        await Assert.ThrowsAsync<UnauthorizedException>(() => service.BindAsync(scenario.Team.Id, scenario.Actor.Id, BindRequest(Guid.NewGuid()), cancellationToken));
        await Assert.ThrowsAsync<UnauthorizedException>(() => service.UnbindAsync(scenario.Team.Id, scenario.Actor.Id, cancellationToken));
        await Assert.ThrowsAsync<UnauthorizedException>(() => service.SyncNowAsync(scenario.Team.Id, scenario.Actor.Id, cancellationToken));
        Assert.Equal(0, client.SearchCallCount);
        Assert.Equal(0, sync.CallCount);
    }

    [Theory]
    [InlineData(TeamMemberRole.Owner)]
    [InlineData(TeamMemberRole.Admin)]
    public async Task OwnerAndAdmin_CanReadStatusAndSearch(TeamMemberRole role)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var scope = factory.Services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var scenario = await SeedAsync(context, role, cancellationToken);
        var external = ExternalTeam();
        var client = new FakeSpbhlClient([external]);
        var service = CreateService(context, client, new FakeSyncService());

        var status = await service.GetStatusAsync(scenario.Team.Id, scenario.Actor.Id, cancellationToken);
        var results = await service.SearchTeamsAsync(scenario.Team.Id, scenario.Actor.Id, "  Ладога  ", cancellationToken);

        Assert.False(status.IsLinked);
        Assert.Equal("Ладога", client.LastSearchTitle);
        Assert.Equal(external.TeamId, Assert.Single(results).TeamId);
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("A")]
    public async Task Search_RejectsInvalidTitleWithoutCallingClient(string title)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var scope = factory.Services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var scenario = await SeedAsync(context, TeamMemberRole.Owner, cancellationToken);
        var client = new FakeSpbhlClient();
        var service = CreateService(context, client, new FakeSyncService());

        await Assert.ThrowsAsync<BusinessRuleException>(() =>
            service.SearchTeamsAsync(scenario.Team.Id, scenario.Actor.Id, title, cancellationToken));

        Assert.Equal(0, client.SearchCallCount);
    }

    [Fact]
    public async Task Bind_UsesCanonicalNameAndReturnsInitialSyncResult()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var scope = factory.Services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var scenario = await SeedAsync(context, TeamMemberRole.Owner, cancellationToken);
        var external = ExternalTeam();
        var client = new FakeSpbhlClient([external]);
        var sync = new FakeSyncService(SyncResult(scenario.Team.Id, external.TeamId));
        var service = CreateService(context, client, sync);
        var request = new BindSpbhlTeamRequest
        {
            SpbhlTeamId = external.TeamId,
            SpbhlTeamName = "МОЯ ФЕЙКОВАЯ КОМАНДА"
        };

        var result = await service.BindAsync(scenario.Team.Id, scenario.Actor.Id, request, cancellationToken);

        context.ChangeTracker.Clear();
        var team = await context.Teams.AsNoTracking().SingleAsync(value => value.Id == scenario.Team.Id, cancellationToken);
        Assert.Equal(external.TeamId, team.SpbhlTeamId);
        Assert.Equal("Ладога", team.SpbhlTeamName);
        Assert.True(result.InitialSyncSucceeded);
        Assert.Equal(2, result.Sync!.CreatedCount);
        Assert.Equal(1, sync.CallCount);
    }

    [Fact]
    public async Task Bind_WhenTeamIdIsNotAuthoritative_DoesNotSaveOrSync()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var scope = factory.Services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var scenario = await SeedAsync(context, TeamMemberRole.Owner, cancellationToken);
        var sync = new FakeSyncService();
        var external = ExternalTeam();
        var service = CreateService(context, new FakeSpbhlClient([external]), sync);
        var request = BindRequest(external.TeamId);
        request.SpbhlTeamId = Guid.NewGuid();

        await Assert.ThrowsAsync<BusinessRuleException>(() =>
            service.BindAsync(scenario.Team.Id, scenario.Actor.Id, request, cancellationToken));

        context.ChangeTracker.Clear();
        Assert.Null((await context.Teams.FindAsync([scenario.Team.Id], cancellationToken))!.SpbhlTeamId);
        Assert.Equal(0, sync.CallCount);
    }

    [Fact]
    public async Task Bind_SameIdentityIsIdempotent_ButDifferentIdentityIsRejected()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var scope = factory.Services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var external = ExternalTeam();
        var scenario = await SeedAsync(context, TeamMemberRole.Owner, cancellationToken, external.TeamId);
        var sync = new FakeSyncService(SyncResult(scenario.Team.Id, external.TeamId));
        var service = CreateService(context, new FakeSpbhlClient([external]), sync);

        await service.BindAsync(scenario.Team.Id, scenario.Actor.Id, BindRequest(external.TeamId), cancellationToken);
        var different = BindRequest(Guid.NewGuid());
        different.SpbhlTeamId = Guid.NewGuid();
        await Assert.ThrowsAsync<BusinessRuleException>(() =>
            service.BindAsync(scenario.Team.Id, scenario.Actor.Id, different, cancellationToken));

        context.ChangeTracker.Clear();
        Assert.Equal(external.TeamId, (await context.Teams.FindAsync([scenario.Team.Id], cancellationToken))!.SpbhlTeamId);
        Assert.Equal(1, sync.CallCount);
    }

    [Fact]
    public async Task Bind_RejectsIdentityAlreadyUsedByAnotherTeam()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var scope = factory.Services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var external = ExternalTeam();
        var scenario = await SeedAsync(context, TeamMemberRole.Owner, cancellationToken);
        context.Teams.Add(NewTeam(Guid.NewGuid(), external.TeamId));
        await context.SaveChangesAsync(cancellationToken);
        var sync = new FakeSyncService();
        var service = CreateService(context, new FakeSpbhlClient([external]), sync);

        await Assert.ThrowsAsync<BusinessRuleException>(() =>
            service.BindAsync(scenario.Team.Id, scenario.Actor.Id, BindRequest(external.TeamId), cancellationToken));

        Assert.Null(scenario.Team.SpbhlTeamId);
        Assert.Equal(0, sync.CallCount);
    }

    [Fact]
    public async Task Bind_WhenInitialSyncTransportFails_KeepsLinkAndReturnsSafeFailure()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var scope = factory.Services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var scenario = await SeedAsync(context, TeamMemberRole.Owner, cancellationToken);
        var external = ExternalTeam();
        var service = CreateService(
            context,
            new FakeSpbhlClient([external]),
            new FakeSyncService(new HttpRequestException("secret upstream body")));

        var result = await service.BindAsync(scenario.Team.Id, scenario.Actor.Id, BindRequest(external.TeamId), cancellationToken);

        context.ChangeTracker.Clear();
        var team = await context.Teams.AsNoTracking().SingleAsync(value => value.Id == scenario.Team.Id, cancellationToken);
        Assert.Equal(external.TeamId, team.SpbhlTeamId);
        Assert.False(result.InitialSyncSucceeded);
        Assert.Null(result.Sync);
        Assert.Equal("Команда привязана, но не удалось загрузить расписание СПбХЛ.", result.SyncError);
        Assert.DoesNotContain("secret", result.SyncError);
    }

    [Fact]
    public async Task Unbind_IsIdempotentAndPreservesImportedEventAttendanceAndScore()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var scope = factory.Services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var external = ExternalTeam();
        var scenario = await SeedAsync(context, TeamMemberRole.Owner, cancellationToken, external.TeamId);
        var scheduledEvent = new ScheduledEvent
        {
            TeamId = scenario.Team.Id,
            Title = "Ладога — АЛГА",
            Type = EventType.Game,
            StartTime = DateTime.UtcNow.AddDays(1),
            Status = EventStatus.Completed,
            LocationName = "Arena",
            LocationAddress = string.Empty,
            SpbhlTournamentId = 6537,
            SpbhlMatchId = 118101,
            HomeScore = 4,
            AwayScore = 2
        };
        var attendance = new Attendance
        {
            Event = scheduledEvent,
            UserId = scenario.Actor.Id,
            Status = AttendanceStatus.Confirmed
        };
        var primaryLink = new TeamExternalLeagueLink
        {
            TeamId = scenario.Team.Id,
            Provider = ExternalLeagueProvider.Spbhl,
            ExternalTeamId = external.TeamId.ToString("D"),
            ExternalTeamName = external.Name,
            IsPrimary = true
        };
        var secondaryLink = new TeamExternalLeagueLink
        {
            TeamId = scenario.Team.Id,
            Provider = ExternalLeagueProvider.Spbhl,
            ExternalTeamId = Guid.NewGuid().ToString("D"),
            ExternalTeamName = "Secondary SPbHL team"
        };
        context.AddRange(scheduledEvent, attendance, primaryLink, secondaryLink);
        await context.SaveChangesAsync(cancellationToken);
        var service = CreateService(context, new FakeSpbhlClient(), new FakeSyncService());

        var first = await service.UnbindAsync(scenario.Team.Id, scenario.Actor.Id, cancellationToken);
        var second = await service.UnbindAsync(scenario.Team.Id, scenario.Actor.Id, cancellationToken);

        context.ChangeTracker.Clear();
        var persistedEvent = await context.Events.AsNoTracking().SingleAsync(value => value.Id == scheduledEvent.Id, cancellationToken);
        Assert.False(first.IsLinked);
        Assert.False(second.IsLinked);
        Assert.Equal(6537, persistedEvent.SpbhlTournamentId);
        Assert.Equal(118101, persistedEvent.SpbhlMatchId);
        Assert.Equal(4, persistedEvent.HomeScore);
        Assert.Equal(2, persistedEvent.AwayScore);
        Assert.True(await context.Attendances.AnyAsync(value => value.Id == attendance.Id, cancellationToken));
        Assert.False(await context.TeamExternalLeagueLinks.AnyAsync(
            value => value.TeamId == scenario.Team.Id && value.Provider == ExternalLeagueProvider.Spbhl,
            cancellationToken));
    }

    [Fact]
    public async Task SyncNow_RequiresLinkAndDelegatesExactlyOnce()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var scope = factory.Services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var scenario = await SeedAsync(context, TeamMemberRole.Owner, cancellationToken);
        var sync = new FakeSyncService();
        var service = CreateService(context, new FakeSpbhlClient(), sync);
        await Assert.ThrowsAsync<BusinessRuleException>(() =>
            service.SyncNowAsync(scenario.Team.Id, scenario.Actor.Id, cancellationToken));

        var externalId = Guid.NewGuid();
        scenario.Team.SpbhlTeamId = externalId;
        await context.SaveChangesAsync(cancellationToken);
        sync.Result = SyncResult(scenario.Team.Id, externalId);
        var result = await service.SyncNowAsync(scenario.Team.Id, scenario.Actor.Id, cancellationToken);

        Assert.Equal(1, sync.CallCount);
        Assert.Equal(2, result.CreatedCount);
    }

    private static SpbhlTeamManagementService CreateService(AppDbContext context, ISpbhlClient client, ISpbhlTeamSyncService sync) =>
        new(context, client, sync, NullLogger<SpbhlTeamManagementService>.Instance);

    private static async Task<(Team Team, User Actor)> SeedAsync(
        AppDbContext context,
        TeamMemberRole? role,
        CancellationToken cancellationToken,
        Guid? linkedId = null)
    {
        var actor = new User { FirstName = "Actor", LastName = "Test", Role = UserRole.Player, AppRole = AppRole.User };
        var team = NewTeam(actor.Id, linkedId);
        context.AddRange(actor, team);
        if (role.HasValue)
        {
            context.TeamMemberships.Add(new TeamMembership { Team = team, User = actor, Role = role.Value });
        }
        await context.SaveChangesAsync(cancellationToken);
        return (team, actor);
    }

    private static Team NewTeam(Guid creatorId, Guid? linkedId = null) => new()
    {
        Name = $"Management {Guid.NewGuid():N}",
        InviteCode = Guid.NewGuid().ToString("N")[..20],
        Visibility = TeamVisibility.Private,
        CreatedByUserId = creatorId,
        SpbhlTeamId = linkedId,
        SpbhlTeamName = linkedId.HasValue ? "Old name" : null,
        SpbhlLastSyncAttemptAt = linkedId.HasValue ? DateTime.UtcNow.AddMinutes(-2) : null,
        SpbhlLastSuccessfulSyncAt = linkedId.HasValue ? DateTime.UtcNow.AddMinutes(-1) : null
    };

    private static BindSpbhlTeamRequest BindRequest(Guid spbhlTeamId) => new()
    {
        SpbhlTeamId = spbhlTeamId,
        SpbhlTeamName = "Ладога"
    };

    private static SpbhlTeamSearchItem ExternalTeam() => new()
    {
        TeamId = Guid.NewGuid(),
        Name = "Ладога",
        ProfileUrl = "https://spbhl.ru/Team"
    };

    private static SpbhlTeamSyncResult SyncResult(Guid teamId, Guid spbhlTeamId) => new()
    {
        TeamId = teamId,
        SpbhlTeamId = spbhlTeamId,
        ReceivedCount = 2,
        CreatedCount = 2,
        SyncedAt = DateTime.UtcNow
    };

    private sealed class FakeSpbhlClient(IReadOnlyCollection<SpbhlTeamSearchItem>? results = null) : ISpbhlClient
    {
        public int SearchCallCount { get; private set; }
        public string? LastSearchTitle { get; private set; }
        public Task<IReadOnlyCollection<SpbhlTeamSearchItem>> SearchTeamsAsync(string? title, CancellationToken cancellationToken)
        {
            SearchCallCount++;
            LastSearchTitle = title;
            return Task.FromResult(results ?? []);
        }
        public Task<IReadOnlyCollection<SpbhlMatchItem>> GetTeamScheduleAsync(Guid teamId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
        public Task<SpbhlMatchDetails?> GetMatchDetailsAsync(int tournamentId, int matchId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
        public Task<SpbhlTeamProfile?> GetTeamProfileAsync(Guid teamId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private sealed class FakeSyncService : ISpbhlTeamSyncService
    {
        private readonly Exception? _exception;
        public FakeSyncService(SpbhlTeamSyncResult? result = null) => Result = result;
        public FakeSyncService(Exception exception) => _exception = exception;
        public int CallCount { get; private set; }
        public SpbhlTeamSyncResult? Result { get; set; }
        public Task<SpbhlTeamSyncResult> SyncTeamAsync(Guid teamId, CancellationToken cancellationToken)
        {
            CallCount++;
            return _exception is null
                ? Task.FromResult(Result ?? SyncResult(teamId, Guid.NewGuid()))
                : Task.FromException<SpbhlTeamSyncResult>(_exception);
        }
    }
}
