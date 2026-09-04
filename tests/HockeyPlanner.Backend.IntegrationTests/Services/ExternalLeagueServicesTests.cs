using HockeyPlanner.Backend.Core.Entities;
using HockeyPlanner.Backend.Core.Enums;
using HockeyPlanner.Backend.Core.Exceptions;
using HockeyPlanner.Backend.Infrastructure.Data;
using HockeyPlanner.Backend.IntegrationTests.Infrastructure;
using HockeyPlanner.Backend.WebAPI.Models.ExternalLeagues;
using HockeyPlanner.Backend.WebAPI.Models.Spbhl;
using HockeyPlanner.Backend.WebAPI.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace HockeyPlanner.Backend.IntegrationTests.Services;

[Collection(IntegrationTestCollection.Name)]
[Trait("Category", "ExternalLeagueServices")]
public sealed class ExternalLeagueServicesTests(HockeyPlannerWebApplicationFactory factory)
{
    [Fact]
    public void ProviderResolver_RejectsDuplicateAndUnsupportedProviders()
    {
        Assert.Throws<ArgumentException>(() => new ExternalLeagueProviderResolver([new FakeProvider(), new FakeProvider()]));

        var resolver = new ExternalLeagueProviderResolver([new FakeProvider()]);
        Assert.Throws<BusinessRuleException>(() => resolver.Resolve((ExternalLeagueProvider)999));
    }

    [Fact]
    public async Task Search_IsTeamIndependent_AndNormalizesTitle()
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var provider = new FakeProvider();
        var service = CreateManagementService(context, provider);

        var result = await service.SearchTeamsAsync(
            ExternalLeagueProvider.Spbhl,
            "  Северная  ",
            TestContext.Current.CancellationToken);

        Assert.Single(result);
        Assert.Equal("Северная", provider.LastSearchTitle);
    }

    [Fact]
    public async Task CreateLinks_AllowsMultipleProfiles_AndMaintainsOnePrimary()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var scope = factory.Services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var scenario = await SeedTeamAsync(context, TeamMemberRole.Owner, cancellationToken);
        var firstId = Guid.NewGuid().ToString("D");
        var secondId = Guid.NewGuid().ToString("D");
        var provider = new FakeProvider(firstId, secondId);
        var service = CreateManagementService(context, provider);

        var first = await service.CreateLinkAsync(scenario.Team.Id, scenario.User.Id,
            new() { Provider = ExternalLeagueProvider.Spbhl, ExternalTeamId = firstId }, cancellationToken);
        var second = await service.CreateLinkAsync(scenario.Team.Id, scenario.User.Id,
            new() { Provider = ExternalLeagueProvider.Spbhl, ExternalTeamId = secondId, IsPrimary = true }, cancellationToken);

        context.ChangeTracker.Clear();
        var links = await context.TeamExternalLeagueLinks.AsNoTracking()
            .Where(value => value.TeamId == scenario.Team.Id)
            .OrderBy(value => value.CreatedAt)
            .ToArrayAsync(cancellationToken);
        var team = await context.Teams.AsNoTracking().SingleAsync(value => value.Id == scenario.Team.Id, cancellationToken);
        Assert.Equal(2, links.Length);
        Assert.False(links.Single(value => value.Id == first.Id).IsPrimary);
        Assert.True(links.Single(value => value.Id == second.Id).IsPrimary);
        Assert.Equal(secondId, team.SpbhlTeamId?.ToString("D"));
        Assert.Equal("Canonical team", team.SpbhlTeamName);
    }

    [Fact]
    public async Task DuplicateExternalProfile_IsIdempotentForSameTeam_AndRejectedForAnotherTeam()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var scope = factory.Services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var first = await SeedTeamAsync(context, TeamMemberRole.Owner, cancellationToken);
        var second = await SeedTeamAsync(context, TeamMemberRole.Owner, cancellationToken);
        var externalId = Guid.NewGuid().ToString("D");
        var service = CreateManagementService(context, new FakeProvider(externalId));
        var request = new CreateExternalLeagueLinkRequest
        {
            Provider = ExternalLeagueProvider.Spbhl,
            ExternalTeamId = externalId
        };

        var created = await service.CreateLinkAsync(first.Team.Id, first.User.Id, request, cancellationToken);
        var repeated = await service.CreateLinkAsync(first.Team.Id, first.User.Id, request, cancellationToken);
        await Assert.ThrowsAsync<BusinessRuleException>(() =>
            service.CreateLinkAsync(second.Team.Id, second.User.Id, request, cancellationToken));

        Assert.Equal(created.Id, repeated.Id);
        Assert.Equal(1, await context.TeamExternalLeagueLinks.CountAsync(
            value => value.Provider == ExternalLeagueProvider.Spbhl && value.ExternalTeamId == externalId,
            cancellationToken));
    }

    [Fact]
    public async Task TwoLinks_SyncUnion_PersistsDistinctMatchesAndAttendanceOnce()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var scope = factory.Services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var scenario = await SeedTeamAsync(context, TeamMemberRole.Owner, cancellationToken, additionalMembers: 2);
        var firstId = Guid.NewGuid().ToString("D");
        var secondId = Guid.NewGuid().ToString("D");
        var firstLink = AddLink(context, scenario.Team.Id, firstId, "Любитель 1", true);
        var secondLink = AddLink(context, scenario.Team.Id, secondId, "Любитель 3", false);
        await context.SaveChangesAsync(cancellationToken);
        var provider = new FakeProvider(firstId, secondId);
        provider.SetSchedule(
            firstId,
            Match(100, 1, "Любитель 1", "Arena A", "Address A"),
            Match(100, 2));
        provider.SetSchedule(
            secondId,
            Match(100, 3, null, "Arena B", "Address B"),
            Match(100, 1));
        var service = CreateSyncService(context, provider);

        var firstRun = await service.SyncTeamExternalLinksAsync(scenario.Team.Id, null, cancellationToken);
        var secondRun = await service.SyncTeamExternalLinksAsync(scenario.Team.Id, null, cancellationToken);

        context.ChangeTracker.Clear();
        var events = await context.Events.AsNoTracking().Where(value => value.TeamId == scenario.Team.Id).ToArrayAsync(cancellationToken);
        Assert.Equal(3, events.Length);
        Assert.Equal(3, firstRun.Sum(value => value.CreatedCount));
        Assert.Equal(0, secondRun.Sum(value => value.CreatedCount));
        Assert.Equal("Любитель 1", events.Single(value => value.SpbhlMatchId == 1).ExternalDivisionName);
        Assert.Equal("Любитель 3", events.Single(value => value.SpbhlMatchId == 3).ExternalDivisionName);
        Assert.Equal("Address B", events.Single(value => value.SpbhlMatchId == 3).LocationAddress);
        Assert.All(events, value => Assert.Equal(ExternalLeagueProvider.Spbhl, value.ExternalLeagueProvider));
        Assert.Equal(9, await context.Attendances.CountAsync(
            value => events.Select(item => item.Id).Contains(value.EventId), cancellationToken));
        Assert.Equal(0, provider.DetailCallCount);
        Assert.NotNull(await context.TeamExternalLeagueLinks.FindAsync([firstLink.Id], cancellationToken));
        Assert.NotNull(await context.TeamExternalLeagueLinks.FindAsync([secondLink.Id], cancellationToken));
    }

    [Fact]
    public async Task SameMatchFromTwoLinks_CreatesOneEventAndOneAttendanceSet()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var scope = factory.Services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var scenario = await SeedTeamAsync(context, TeamMemberRole.Owner, cancellationToken, additionalMembers: 1);
        var firstId = Guid.NewGuid().ToString("D");
        var secondId = Guid.NewGuid().ToString("D");
        AddLink(context, scenario.Team.Id, firstId, null, true);
        AddLink(context, scenario.Team.Id, secondId, null, false);
        await context.SaveChangesAsync(cancellationToken);
        var provider = new FakeProvider(firstId, secondId);
        provider.SetSchedule(firstId, Match(200, 20));
        provider.SetSchedule(secondId, Match(200, 20));

        await CreateSyncService(context, provider).SyncTeamExternalLinksAsync(scenario.Team.Id, null, cancellationToken);

        var scheduledEvent = await context.Events.SingleAsync(value => value.TeamId == scenario.Team.Id, cancellationToken);
        Assert.Equal(2, await context.Attendances.CountAsync(value => value.EventId == scheduledEvent.Id, cancellationToken));
    }

    [Fact]
    public async Task LegacySyncFacade_SynchronizesAllSpbhlLinks()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var scope = factory.Services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var scenario = await SeedTeamAsync(context, TeamMemberRole.Owner, cancellationToken);
        var firstId = Guid.NewGuid().ToString("D");
        var secondId = Guid.NewGuid().ToString("D");
        AddLink(context, scenario.Team.Id, firstId, null, true);
        AddLink(context, scenario.Team.Id, secondId, null, false);
        await context.SaveChangesAsync(cancellationToken);
        var provider = new FakeProvider(firstId, secondId);
        provider.SetSchedule(firstId, Match(210, 21));
        provider.SetSchedule(secondId, Match(210, 22));
        var genericSync = CreateSyncService(context, provider);
        var legacyFacade = new SpbhlTeamSyncService(
            context,
            new UnusedSpbhlClient(),
            genericSync,
            NullLogger<SpbhlTeamSyncService>.Instance);

        var result = await legacyFacade.SyncTeamAsync(scenario.Team.Id, cancellationToken);

        Assert.Equal(2, result.ReceivedCount);
        Assert.Equal(2, result.CreatedCount);
        Assert.Equal(2, provider.ScheduleCallCount);
        Assert.Equal(2, await context.Events.CountAsync(value => value.TeamId == scenario.Team.Id, cancellationToken));
    }

    [Fact]
    public async Task FinishedMatchWithoutScheduleScore_UsesOneDetailRequest()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var scope = factory.Services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var scenario = await SeedTeamAsync(context, TeamMemberRole.Owner, cancellationToken);
        var externalId = Guid.NewGuid().ToString("D");
        var link = AddLink(context, scenario.Team.Id, externalId, null, true);
        await context.SaveChangesAsync(cancellationToken);
        var provider = new FakeProvider(externalId);
        var match = Match(300, 30);
        match.Status = ExternalMatchStatus.Finished;
        provider.SetSchedule(externalId, match);
        provider.Details = new ExternalMatchDetails
        {
            ExternalCompetitionId = "300",
            ExternalMatchId = "30",
            HomeScore = 4,
            AwayScore = 2,
            Status = ExternalMatchStatus.Finished,
            ArenaAddress = "Detail address"
        };

        var result = await CreateSyncService(context, provider).SyncExternalLinkAsync(link.Id, cancellationToken);

        var scheduledEvent = await context.Events.SingleAsync(value => value.TeamId == scenario.Team.Id, cancellationToken);
        Assert.Equal(1, result.EnrichmentRequestCount);
        Assert.Equal(1, provider.DetailCallCount);
        Assert.Equal(4, scheduledEvent.HomeScore);
        Assert.Equal(2, scheduledEvent.AwayScore);
        Assert.Equal("Detail address", scheduledEvent.LocationAddress);
        Assert.Equal(EventStatus.Completed, scheduledEvent.Status);
    }

    [Fact]
    public async Task Sync_UsesStringExternalIdentityWithoutNumericIds()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var scope = factory.Services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var scenario = await SeedTeamAsync(context, TeamMemberRole.Owner, cancellationToken);
        var externalId = Guid.NewGuid().ToString("D");
        var link = AddLink(context, scenario.Team.Id, externalId, null, true);
        await context.SaveChangesAsync(cancellationToken);
        var provider = new FakeProvider(externalId);
        provider.SetSchedule(externalId, new ExternalMatch
        {
            ExternalCompetitionId = "winter-cup-2027",
            ExternalMatchId = "game-A12",
            StartTime = new DateTimeOffset(2027, 1, 5, 20, 0, 0, TimeSpan.FromHours(3)),
            HomeTeamName = "Home",
            AwayTeamName = "Away",
            MatchUrl = "https://league.example/matches/game-A12",
            Status = ExternalMatchStatus.Scheduled
        });

        await CreateSyncService(context, provider).SyncExternalLinkAsync(link.Id, cancellationToken);

        var scheduledEvent = await context.Events.AsNoTracking()
            .SingleAsync(value => value.TeamId == scenario.Team.Id, cancellationToken);
        Assert.Equal("winter-cup-2027", scheduledEvent.ExternalCompetitionId);
        Assert.Equal("game-A12", scheduledEvent.ExternalMatchId);
        Assert.Null(scheduledEvent.SpbhlTournamentId);
        Assert.Null(scheduledEvent.SpbhlMatchId);
    }

    [Fact]
    public async Task DeletePrimary_SelectsOldestReplacement_AndKeepsImportedEvent()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var scope = factory.Services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var scenario = await SeedTeamAsync(context, TeamMemberRole.Owner, cancellationToken);
        var first = AddLink(context, scenario.Team.Id, Guid.NewGuid().ToString("D"), null, true, DateTime.UtcNow.AddMinutes(-2));
        var replacement = AddLink(context, scenario.Team.Id, Guid.NewGuid().ToString("D"), null, false, DateTime.UtcNow.AddMinutes(-1));
        var imported = StoredEvent(scenario.Team.Id, 400, 40);
        context.Events.Add(imported);
        await context.SaveChangesAsync(cancellationToken);
        var provider = new FakeProvider(replacement.ExternalTeamId);
        provider.SetSchedule(replacement.ExternalTeamId, Match(401, 41));
        var service = CreateManagementService(context, provider);

        await service.DeleteLinkAsync(scenario.Team.Id, first.Id, scenario.User.Id, cancellationToken);
        await service.SyncTeamAsync(scenario.Team.Id, scenario.User.Id, cancellationToken);

        context.ChangeTracker.Clear();
        Assert.True((await context.TeamExternalLeagueLinks.FindAsync([replacement.Id], cancellationToken))!.IsPrimary);
        var team = await context.Teams.AsNoTracking().SingleAsync(value => value.Id == scenario.Team.Id, cancellationToken);
        Assert.Equal(replacement.ExternalTeamId, team.SpbhlTeamId?.ToString("D"));
        Assert.NotNull(await context.Events.FindAsync([imported.Id], cancellationToken));
        Assert.Equal(2, await context.Events.CountAsync(value => value.TeamId == scenario.Team.Id, cancellationToken));
    }

    [Fact]
    public async Task LinkRemovedDuringHttp_RejectsStaleSchedule()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var syncScope = factory.Services.CreateAsyncScope();
        var syncContext = syncScope.ServiceProvider.GetRequiredService<AppDbContext>();
        var scenario = await SeedTeamAsync(syncContext, TeamMemberRole.Owner, cancellationToken);
        var externalId = Guid.NewGuid().ToString("D");
        var link = AddLink(syncContext, scenario.Team.Id, externalId, null, true);
        await syncContext.SaveChangesAsync(cancellationToken);
        var provider = new BlockingProvider(externalId, Match(500, 50));
        var syncTask = CreateSyncService(syncContext, provider).SyncExternalLinkAsync(link.Id, cancellationToken);
        await provider.RequestStarted.WaitAsync(cancellationToken);

        await using (var mutationScope = factory.Services.CreateAsyncScope())
        {
            var mutationContext = mutationScope.ServiceProvider.GetRequiredService<AppDbContext>();
            await CreateManagementService(mutationContext, new FakeProvider())
                .DeleteLinkAsync(scenario.Team.Id, link.Id, scenario.User.Id, cancellationToken);
        }

        provider.Complete();
        await Assert.ThrowsAsync<BusinessRuleException>(() => syncTask);

        await using var assertionScope = factory.Services.CreateAsyncScope();
        var assertionContext = assertionScope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.False(await assertionContext.Events.AnyAsync(value => value.TeamId == scenario.Team.Id, cancellationToken));
    }

    [Fact]
    public async Task LinkIdentityChangedDuringHttp_PreservesAttemptAndRejectsStaleSchedule()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var syncScope = factory.Services.CreateAsyncScope();
        var syncContext = syncScope.ServiceProvider.GetRequiredService<AppDbContext>();
        var scenario = await SeedTeamAsync(syncContext, TeamMemberRole.Owner, cancellationToken);
        var externalId = Guid.NewGuid().ToString("D");
        var link = AddLink(syncContext, scenario.Team.Id, externalId, null, true);
        await syncContext.SaveChangesAsync(cancellationToken);
        var provider = new BlockingProvider(externalId, Match(501, 51));
        var syncTask = CreateSyncService(syncContext, provider).SyncExternalLinkAsync(link.Id, cancellationToken);
        await provider.RequestStarted.WaitAsync(cancellationToken);

        await using (var mutationScope = factory.Services.CreateAsyncScope())
        {
            var mutationContext = mutationScope.ServiceProvider.GetRequiredService<AppDbContext>();
            var current = await mutationContext.TeamExternalLeagueLinks
                .SingleAsync(value => value.Id == link.Id, cancellationToken);
            current.ExternalTeamId = Guid.NewGuid().ToString("D");
            await mutationContext.SaveChangesAsync(cancellationToken);
        }

        provider.Complete();
        await Assert.ThrowsAsync<BusinessRuleException>(() => syncTask);

        await using var assertionScope = factory.Services.CreateAsyncScope();
        var assertionContext = assertionScope.ServiceProvider.GetRequiredService<AppDbContext>();
        var persistedLink = await assertionContext.TeamExternalLeagueLinks.AsNoTracking()
            .SingleAsync(value => value.Id == link.Id, cancellationToken);
        Assert.NotNull(persistedLink.LastSyncAttemptAt);
        Assert.Null(persistedLink.LastSuccessfulSyncAt);
        Assert.False(await assertionContext.Events.AnyAsync(value => value.TeamId == scenario.Team.Id, cancellationToken));
    }

    [Fact]
    public async Task Update_PreservesDescriptionAndAttendanceResponse()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var scope = factory.Services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var scenario = await SeedTeamAsync(context, TeamMemberRole.Owner, cancellationToken);
        var externalId = Guid.NewGuid().ToString("D");
        var link = AddLink(context, scenario.Team.Id, externalId, null, true);
        var stored = StoredEvent(scenario.Team.Id, 600, 60);
        stored.Description = "Internal description";
        stored.Attendances.Add(new Attendance
        {
            EventId = stored.Id,
            UserId = scenario.User.Id,
            Status = AttendanceStatus.Confirmed,
            RespondedAt = DateTime.UtcNow
        });
        context.Add(stored);
        await context.SaveChangesAsync(cancellationToken);
        var attendanceId = stored.Attendances.Single().Id;
        var provider = new FakeProvider(externalId);
        var changed = Match(600, 60, arena: "New arena", address: "New address");
        changed.StartTime = changed.StartTime.AddDays(1);
        provider.SetSchedule(externalId, changed);

        await CreateSyncService(context, provider).SyncExternalLinkAsync(link.Id, cancellationToken);

        context.ChangeTracker.Clear();
        var persisted = await context.Events.AsNoTracking().SingleAsync(value => value.Id == stored.Id, cancellationToken);
        var attendance = await context.Attendances.AsNoTracking().SingleAsync(value => value.Id == attendanceId, cancellationToken);
        Assert.Equal("Internal description", persisted.Description);
        Assert.Equal("New arena", persisted.LocationName);
        Assert.Equal(AttendanceStatus.Confirmed, attendance.Status);
    }

    private static ExternalLeagueManagementService CreateManagementService(AppDbContext context, IExternalLeagueProvider provider)
    {
        var resolver = new ExternalLeagueProviderResolver([provider]);
        var sync = CreateSyncService(context, provider);
        return new ExternalLeagueManagementService(context, resolver, sync);
    }

    private static ExternalLeagueSyncService CreateSyncService(AppDbContext context, IExternalLeagueProvider provider) =>
        new(context, new ExternalLeagueProviderResolver([provider]), NullLogger<ExternalLeagueSyncService>.Instance);

    private static async Task<(User User, Team Team)> SeedTeamAsync(
        AppDbContext context,
        TeamMemberRole role,
        CancellationToken cancellationToken,
        int additionalMembers = 0)
    {
        var user = new User { FirstName = "External", LastName = "Owner", Role = UserRole.Player, AppRole = AppRole.User };
        var team = new Team
        {
            Name = $"External test {Guid.NewGuid():N}",
            InviteCode = Guid.NewGuid().ToString("N")[..20],
            Visibility = TeamVisibility.Private,
            CreatedByUserId = user.Id
        };
        context.AddRange(user, team);
        context.TeamMemberships.Add(new TeamMembership { Team = team, User = user, Role = role });
        for (var index = 0; index < additionalMembers; index++)
        {
            var member = new User { FirstName = $"Member{index}", LastName = "External", Role = UserRole.Player, AppRole = AppRole.User };
            context.Users.Add(member);
            context.TeamMemberships.Add(new TeamMembership { Team = team, User = member, Role = TeamMemberRole.Member });
        }
        await context.SaveChangesAsync(cancellationToken);
        return (user, team);
    }

    private static TeamExternalLeagueLink AddLink(
        AppDbContext context,
        Guid teamId,
        string externalId,
        string? division,
        bool primary,
        DateTime? createdAt = null)
    {
        var link = new TeamExternalLeagueLink
        {
            TeamId = teamId,
            Provider = ExternalLeagueProvider.Spbhl,
            ExternalTeamId = externalId,
            ExternalTeamName = "Canonical team",
            DivisionName = division,
            IsPrimary = primary,
            CreatedAt = createdAt ?? DateTime.UtcNow
        };
        context.TeamExternalLeagueLinks.Add(link);
        return link;
    }

    private static ExternalMatch Match(
        int tournamentId,
        int matchId,
        string? division = null,
        string? arena = null,
        string? address = null) => new()
    {
        ExternalCompetitionId = tournamentId.ToString(),
        ExternalMatchId = matchId.ToString(),
        LegacyNumericCompetitionId = tournamentId,
        LegacyNumericMatchId = matchId,
        StartTime = new DateTimeOffset(2026, 9, 6, 19, 0, 0, TimeSpan.FromHours(3)),
        HomeTeamName = "Северная столица",
        AwayTeamName = "Соперник",
        ArenaName = arena,
        ArenaAddress = address,
        TournamentName = "Турнир",
        DivisionName = division,
        Status = ExternalMatchStatus.Scheduled,
        MatchUrl = $"https://spbhl.ru/Match?TournamentID={tournamentId}&MatchID={matchId}"
    };

    private static ScheduledEvent StoredEvent(Guid teamId, int tournamentId, int matchId) => new()
    {
        TeamId = teamId,
        Title = "Old title",
        Type = EventType.Game,
        StartTime = DateTime.UtcNow,
        DurationMinutes = 75,
        Status = EventStatus.Scheduled,
        LocationName = "Old arena",
        LocationAddress = "Old address",
        HomeTeamName = "Old home",
        AwayTeamName = "Old away",
        ExternalLeagueProvider = ExternalLeagueProvider.Spbhl,
        ExternalCompetitionId = tournamentId.ToString(),
        ExternalMatchId = matchId.ToString(),
        ExternalMatchUrl = "https://spbhl.ru/Match",
        SpbhlTournamentId = tournamentId,
        SpbhlMatchId = matchId,
        SpbhlMatchUrl = "https://spbhl.ru/Match"
    };

    private class FakeProvider(params string[] profileIds) : IExternalLeagueProvider
    {
        private readonly Dictionary<string, IReadOnlyCollection<ExternalMatch>> _schedules = new(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _profileIds = new(profileIds, StringComparer.OrdinalIgnoreCase);
        public ExternalLeagueProvider Provider => ExternalLeagueProvider.Spbhl;
        public string? LastSearchTitle { get; private set; }
        public ExternalMatchDetails? Details { get; set; }
        public int DetailCallCount { get; private set; }
        public int ScheduleCallCount { get; private set; }

        public Task<IReadOnlyCollection<ExternalTeamSearchItem>> SearchTeamsAsync(string title, CancellationToken cancellationToken)
        {
            LastSearchTitle = title;
            return Task.FromResult<IReadOnlyCollection<ExternalTeamSearchItem>>([new()
            {
                Provider = Provider,
                ExternalTeamId = Guid.NewGuid().ToString("D"),
                Name = "Северная"
            }]);
        }

        public Task<ExternalTeamProfile?> GetTeamProfileAsync(string externalTeamId, CancellationToken cancellationToken)
        {
            ExternalTeamProfile? result = _profileIds.Contains(externalTeamId) ? new()
            {
                Provider = Provider,
                ExternalTeamId = externalTeamId,
                Name = "Canonical team",
                DivisionName = "Division",
                ProfileUrl = $"https://spbhl.ru/Team?TeamID={externalTeamId}",
                LogoUrl = "https://spbhl.ru/logo.png",
                City = "Санкт-Петербург",
                Country = "Россия"
            } : null;
            return Task.FromResult(result);
        }

        public Task<IReadOnlyCollection<ExternalMatch>> GetTeamScheduleAsync(string externalTeamId, CancellationToken cancellationToken)
        {
            ScheduleCallCount++;
            return Task.FromResult(_schedules.GetValueOrDefault(externalTeamId) ?? []);
        }

        public Task<ExternalMatchDetails?> GetMatchDetailsAsync(
            string externalCompetitionId,
            string externalMatchId,
            CancellationToken cancellationToken)
        {
            DetailCallCount++;
            return Task.FromResult(Details);
        }

        public void SetSchedule(string externalTeamId, params ExternalMatch[] matches) => _schedules[externalTeamId] = matches;
    }

    private sealed class BlockingProvider(string externalTeamId, ExternalMatch match) : IExternalLeagueProvider
    {
        private readonly TaskCompletionSource _started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<IReadOnlyCollection<ExternalMatch>> _result =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ExternalLeagueProvider Provider => ExternalLeagueProvider.Spbhl;
        public Task RequestStarted => _started.Task;
        public void Complete() => _result.TrySetResult([match]);
        public Task<IReadOnlyCollection<ExternalTeamSearchItem>> SearchTeamsAsync(string title, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<ExternalTeamProfile?> GetTeamProfileAsync(string id, CancellationToken cancellationToken) => throw new NotSupportedException();
        public async Task<IReadOnlyCollection<ExternalMatch>> GetTeamScheduleAsync(string id, CancellationToken cancellationToken)
        {
            Assert.Equal(externalTeamId, id);
            _started.TrySetResult();
            return await _result.Task.WaitAsync(cancellationToken);
        }
        public Task<ExternalMatchDetails?> GetMatchDetailsAsync(
            string externalCompetitionId,
            string externalMatchId,
            CancellationToken cancellationToken) =>
            Task.FromResult<ExternalMatchDetails?>(null);
    }

    private sealed class UnusedSpbhlClient : ISpbhlClient
    {
        public Task<IReadOnlyCollection<SpbhlTeamSearchItem>> SearchTeamsAsync(string? title, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
        public Task<IReadOnlyCollection<SpbhlMatchItem>> GetTeamScheduleAsync(Guid teamId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
        public Task<SpbhlMatchDetails?> GetMatchDetailsAsync(int tournamentId, int matchId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
        public Task<SpbhlTeamProfile?> GetTeamProfileAsync(Guid teamId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }
}
