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
using System.Text.Json;
using HockeyPlanner.Backend.WebAPI.Models.Teams;

namespace HockeyPlanner.Backend.IntegrationTests.Services;

[Collection(IntegrationTestCollection.Name)]
[Trait("Category", "ExternalLeagueServices")]
public sealed class ExternalLeagueServicesTests(HockeyPlannerWebApplicationFactory factory)
{
    [Fact]
    public async Task Sync_ReportsOnlyExistingEventTransitionsToRescheduled()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var scope = factory.Services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var scenario = await SeedTeamAsync(context, TeamMemberRole.Owner, cancellationToken);
        var externalId = Guid.NewGuid().ToString("D");
        var link = AddLink(context, scenario.Team.Id, externalId, null, true);
        var stored = StoredEvent(scenario.Team.Id, 9001, 9002);
        context.Events.Add(stored);
        await context.SaveChangesAsync(cancellationToken);
        var provider = new FakeProvider(externalId);
        var rescheduled = Match(9001, 9002);
        rescheduled.Status = ExternalMatchStatus.Rescheduled;
        rescheduled.StartTime = rescheduled.StartTime.AddDays(2);
        provider.SetSchedule(externalId, rescheduled);

        var first = await CreateSyncService(context, provider).SyncExternalLinkAsync(link.Id, cancellationToken);
        var change = Assert.Single(first.Changes);
        Assert.Equal(stored.Id, change.EventId);
        Assert.Equal(EventStatus.Scheduled, change.PreviousStatus);
        Assert.Equal(EventStatus.Rescheduled, change.NewStatus);

        var second = await CreateSyncService(context, provider).SyncExternalLinkAsync(link.Id, cancellationToken);
        Assert.Empty(second.Changes);
    }

    [Fact]
    public async Task InitialRescheduledImport_DoesNotReportTransition()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var scope = factory.Services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var scenario = await SeedTeamAsync(context, TeamMemberRole.Owner, cancellationToken);
        var externalId = Guid.NewGuid().ToString("D");
        var link = AddLink(context, scenario.Team.Id, externalId, null, true);
        await context.SaveChangesAsync(cancellationToken);
        var provider = new FakeProvider(externalId);
        var match = Match(9011, 9012);
        match.Status = ExternalMatchStatus.Rescheduled;
        provider.SetSchedule(externalId, match);

        var result = await CreateSyncService(context, provider).SyncExternalLinkAsync(link.Id, cancellationToken);
        Assert.Empty(result.Changes);
        Assert.Equal(EventStatus.Rescheduled, (await context.Events.AsNoTracking().SingleAsync(value => value.TeamId == scenario.Team.Id, cancellationToken)).Status);
    }

    [Fact]
    public async Task CompletedScoreUpdate_DoesNotReportRescheduleTransition()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var scope = factory.Services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var scenario = await SeedTeamAsync(context, TeamMemberRole.Owner, cancellationToken);
        var externalId = Guid.NewGuid().ToString("D");
        var link = AddLink(context, scenario.Team.Id, externalId, null, true);
        var stored = StoredEvent(scenario.Team.Id, 9021, 9022);
        stored.Status = EventStatus.Completed;
        context.Events.Add(stored);
        await context.SaveChangesAsync(cancellationToken);
        var provider = new FakeProvider(externalId);
        var match = Match(9021, 9022);
        match.Status = ExternalMatchStatus.Finished;
        match.HomeScore = 4;
        match.AwayScore = 2;
        provider.SetSchedule(externalId, match);

        var result = await CreateSyncService(context, provider).SyncExternalLinkAsync(link.Id, cancellationToken);
        Assert.Empty(result.Changes);
    }

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
    public async Task ApplyProfile_UsesStoredAuthoritativeFields_AndOnlyRequestedValues()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var scope = factory.Services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var scenario = await SeedTeamAsync(context, TeamMemberRole.Owner, cancellationToken);
        scenario.Team.Name = "Local team";
        scenario.Team.AvatarUrl = "https://local/avatar.png";
        scenario.Team.CoverImageUrl = "https://local/cover.png";
        scenario.Team.Description = "Keep description";
        var link = AddLink(context, scenario.Team.Id, Guid.NewGuid().ToString("D"), "Любитель 1", true);
        link.ExternalTeamName = "Official team";
        link.LogoUrl = "https://spbhl.ru/logo.png";
        link.CoverUrl = "https://spbhl.ru/cover.jpg";
        link.City = "Санкт-Петербург";
        link.Country = "Россия";
        await context.SaveChangesAsync(cancellationToken);
        var service = CreateManagementService(context, new FakeProvider());

        var result = await service.ApplyProfileAsync(
            scenario.Team.Id,
            link.Id,
            scenario.User.Id,
            new() { UseName = true, UseLogo = false, UseCover = true },
            cancellationToken);

        context.ChangeTracker.Clear();
        var team = await context.Teams.AsNoTracking().SingleAsync(value => value.Id == scenario.Team.Id, cancellationToken);
        Assert.Equal("Official team", team.Name);
        Assert.Equal("https://local/avatar.png", team.AvatarUrl);
        Assert.Equal("https://spbhl.ru/cover.jpg", team.CoverImageUrl);
        Assert.Equal("Keep description", team.Description);
        Assert.Equal(team.Name, result.Name);
        Assert.Equal(team.AvatarUrl, result.AvatarUrl);
        Assert.Equal(team.CoverImageUrl, result.CoverImageUrl);
    }

    [Fact]
    public async Task ApplyProfile_MergesStructuredCandidates_AndGeneratedMetadataIdempotently()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var scope = factory.Services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var scenario = await SeedTeamAsync(context, TeamMemberRole.Owner, cancellationToken);
        scenario.Team.Description = "Пользовательское описание";
        scenario.Team.PhoneContactsJson = JsonSerializer.Serialize(new[] { new TeamContactItemDto { Title = "Менеджер", Value = "8 (911) 139-02-69" } });
        scenario.Team.LinkContactsJson = JsonSerializer.Serialize(new[] { new TeamContactItemDto { Title = "Сайт", Value = "http://club.example/" } });
        scenario.Team.AddressContactsJson = JsonSerializer.Serialize(new[] { new TeamContactItemDto { Title = "Офис", Value = "Старый адрес" } });
        var link = AddLink(context, scenario.Team.Id, Guid.NewGuid().ToString("D"), null, true);
        link.FoundedYear = 2015;
        link.CoachName = "Иванов Иван";
        link.AdministratorName = "Петров Петр";
        link.PhonesJson = JsonSerializer.Serialize(new[] { "8 (911) 139-02-69", "+7 (921) 111-22-33" });
        link.WebsiteUrlsJson = JsonSerializer.Serialize(new[] { "https://club.example", "https://official.example" });
        context.Events.AddRange(
            ExternalVenueEvent(scenario.Team.Id, "Ледовый комплекс", "Санкт-Петербург, Арена 1"),
            ExternalVenueEvent(scenario.Team.Id, "  ледовый   комплекс ", " санкт-Петербург,  Арена 1 "));
        await context.SaveChangesAsync(cancellationToken);
        var service = CreateManagementService(context, new FakeProvider());
        var linkDto = Assert.Single(await service.GetLinksAsync(scenario.Team.Id, scenario.User.Id, cancellationToken));
        var phoneIds = linkDto.PhoneCandidates.Select(value => value.CandidateId).ToArray();
        var websiteIds = linkDto.WebsiteCandidates.Select(value => value.CandidateId).ToArray();
        Assert.All(linkDto.PhoneCandidates, value => Assert.Equal("Администратор", value.Label));
        Assert.All(linkDto.WebsiteCandidates, value => Assert.Equal("Сайт команды", value.Label));
        var address = Assert.Single(await service.GetAddressCandidatesAsync(scenario.Team.Id, scenario.User.Id, cancellationToken));
        Assert.Equal(2, address.MatchCount);

        var request = new ApplyExternalLeagueProfileRequest
        {
            UseDescriptionMetadata = true,
            SelectedPhoneCandidateIds = phoneIds,
            SelectedWebsiteCandidateIds = websiteIds,
            SelectedAddressCandidateIds = [address.CandidateId]
        };
        await service.ApplyProfileAsync(scenario.Team.Id, link.Id, scenario.User.Id, request, cancellationToken);
        await service.ApplyProfileAsync(scenario.Team.Id, link.Id, scenario.User.Id, request, cancellationToken);

        context.ChangeTracker.Clear();
        var team = await context.Teams.AsNoTracking().SingleAsync(value => value.Id == scenario.Team.Id, cancellationToken);
        var phones = JsonSerializer.Deserialize<List<TeamContactItemDto>>(team.PhoneContactsJson!);
        var websites = JsonSerializer.Deserialize<List<TeamContactItemDto>>(team.LinkContactsJson!);
        var addresses = JsonSerializer.Deserialize<List<TeamContactItemDto>>(team.AddressContactsJson!);
        Assert.Equal(2, phones!.Count);
        Assert.Equal("Администратор", phones.Single(value => value.Value.Contains("921", StringComparison.Ordinal)).Title);
        Assert.Equal(2, websites!.Count);
        Assert.Equal("Сайт команды", websites.Single(value => value.Value.Contains("official.example", StringComparison.Ordinal)).Title);
        Assert.Equal(2, addresses!.Count);
        Assert.Equal("Ледовый комплекс", addresses.Single(value => value.Value.Contains("Арена 1", StringComparison.Ordinal)).Title);
        Assert.StartsWith("Пользовательское описание", team.Description, StringComparison.Ordinal);
        Assert.Equal(1, CountOccurrences(team.Description!, "Официальный профиль:"));
        Assert.Contains("Год создания: 2015", team.Description);
        Assert.DoesNotContain("club.example", team.Description);
        Assert.DoesNotContain("Арена 1", team.Description);
    }

    [Fact]
    public async Task ApplyProfile_InvalidCandidate_IsAtomic()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var scope = factory.Services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var scenario = await SeedTeamAsync(context, TeamMemberRole.Owner, cancellationToken);
        var originalName = scenario.Team.Name;
        var link = AddLink(context, scenario.Team.Id, Guid.NewGuid().ToString("D"), null, true);
        link.ExternalTeamName = "Новое имя";
        await context.SaveChangesAsync(cancellationToken);
        var service = CreateManagementService(context, new FakeProvider());

        await Assert.ThrowsAsync<BusinessRuleException>(() => service.ApplyProfileAsync(
            scenario.Team.Id,
            link.Id,
            scenario.User.Id,
            new ApplyExternalLeagueProfileRequest { UseName = true, SelectedAddressCandidateIds = ["invalid"] },
            cancellationToken));

        context.ChangeTracker.Clear();
        Assert.Equal(originalName, (await context.Teams.AsNoTracking().SingleAsync(value => value.Id == scenario.Team.Id, cancellationToken)).Name);
    }

    [Fact]
    public async Task DuplicateExternalProfile_IsIdempotentForSameTeam_AndAllowedForAnotherTeam()
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
        var other = await service.CreateLinkAsync(second.Team.Id, second.User.Id, request, cancellationToken);
        Assert.NotEqual(created.Id, other.Id);

        Assert.Equal(created.Id, repeated.Id);
        Assert.Equal(2, await context.TeamExternalLeagueLinks.CountAsync(
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
            Match(100, 2, arena: "Arena C", address: "Address C"));
        provider.SetSchedule(
            secondId,
            Match(100, 3, null, "Arena B", "Address B"),
            Match(100, 1, arena: "Arena A", address: "Address A"));
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
    public async Task SyncOne_AddSecondLink_ThenSyncAll_PreservesUnionWithoutDuplicates()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var scope = factory.Services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var scenario = await SeedTeamAsync(context, TeamMemberRole.Owner, cancellationToken);
        var firstId = Guid.NewGuid().ToString("D");
        var secondId = Guid.NewGuid().ToString("D");
        var provider = new FakeProvider(firstId, secondId);
        provider.SetSchedule(firstId, Match(220, 1), Match(220, 2));
        provider.SetSchedule(secondId, Match(220, 2), Match(220, 3));
        var service = CreateManagementService(context, provider);

        var firstLink = await service.CreateLinkAsync(
            scenario.Team.Id,
            scenario.User.Id,
            new() { Provider = ExternalLeagueProvider.Spbhl, ExternalTeamId = firstId },
            cancellationToken);
        await service.SyncLinkAsync(scenario.Team.Id, firstLink.Id, scenario.User.Id, cancellationToken);

        await service.CreateLinkAsync(
            scenario.Team.Id,
            scenario.User.Id,
            new() { Provider = ExternalLeagueProvider.Spbhl, ExternalTeamId = secondId },
            cancellationToken);
        var results = await service.SyncTeamAsync(scenario.Team.Id, scenario.User.Id, cancellationToken);

        context.ChangeTracker.Clear();
        var events = await context.Events.AsNoTracking()
            .Where(value => value.TeamId == scenario.Team.Id)
            .OrderBy(value => value.ExternalMatchId)
            .ToArrayAsync(cancellationToken);
        Assert.Equal(2, results.Count);
        Assert.Equal(3, events.Length);
        Assert.Equal(["1", "2", "3"], events.Select(value => value.ExternalMatchId));
        Assert.Equal(1, events.Count(value => value.ExternalMatchId == "2"));
        Assert.Equal(2, await context.TeamExternalLeagueLinks.CountAsync(
            value => value.TeamId == scenario.Team.Id,
            cancellationToken));
    }

    [Fact]
    public async Task SyncLink_LoadsProfileAndScheduleConcurrently()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var scope = factory.Services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var scenario = await SeedTeamAsync(context, TeamMemberRole.Owner, cancellationToken);
        var externalId = Guid.NewGuid().ToString("D");
        var link = AddLink(context, scenario.Team.Id, externalId, null, true);
        await context.SaveChangesAsync(cancellationToken);
        var provider = new ConcurrentProfileScheduleProvider(externalId);

        var result = await CreateSyncService(context, provider).SyncExternalLinkAsync(link.Id, cancellationToken);

        Assert.Equal(1, result.ReceivedCount);
        Assert.True(provider.ProfileStarted);
        Assert.True(provider.ScheduleStarted);
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
    public async Task ScheduledMatchWithoutAddress_UsesDetailsOnce()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var scope = factory.Services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var scenario = await SeedTeamAsync(context, TeamMemberRole.Owner, cancellationToken);
        var externalId = Guid.NewGuid().ToString("D");
        var link = AddLink(context, scenario.Team.Id, externalId, null, true);
        await context.SaveChangesAsync(cancellationToken);
        var provider = new FakeProvider(externalId);
        provider.SetSchedule(externalId, Match(301, 31, arena: "Короткое имя", address: null));
        provider.Details = new ExternalMatchDetails
        {
            ExternalCompetitionId = "301",
            ExternalMatchId = "31",
            ArenaName = "Полное имя арены",
            ArenaAddress = "Полный адрес"
        };

        var result = await CreateSyncService(context, provider).SyncExternalLinkAsync(link.Id, cancellationToken);

        var scheduledEvent = await context.Events.AsNoTracking().SingleAsync(value => value.TeamId == scenario.Team.Id, cancellationToken);
        Assert.Equal(1, result.EnrichmentRequestCount);
        Assert.Equal(1, provider.DetailCallCount);
        Assert.Equal("Короткое имя", scheduledEvent.LocationName);
        Assert.Equal("Полный адрес", scheduledEvent.LocationAddress);
    }

    [Fact]
    public async Task FixtureThroughSpbhlProvider_PersistsFullArenaAddress()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var scope = factory.Services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var scenario = await SeedTeamAsync(context, TeamMemberRole.Owner, cancellationToken);
        var externalId = Guid.NewGuid();
        var link = AddLink(context, scenario.Team.Id, externalId.ToString("D"), null, true);
        await context.SaveChangesAsync(cancellationToken);
        var client = new FixtureSpbhlClient(new SpbhlScheduleHtmlParser().ParseSchedule(ReadFixture("schedule-rescheduled.html")));
        var provider = new SpbhlExternalLeagueProvider(client);

        await CreateSyncService(context, provider).SyncExternalLinkAsync(link.Id, cancellationToken);

        var scheduledEvent = await context.Events.AsNoTracking().SingleAsync(value => value.TeamId == scenario.Team.Id, cancellationToken);
        Assert.Equal("Ледовый комплекс «АСК-С»", scheduledEvent.LocationName);
        Assert.Equal("Санкт-Петербург, Стрельна, Фронтовая ул., 3", scheduledEvent.LocationAddress);
    }

    [Fact]
    public async Task StatusMapping_RescheduledIsPersisted_AndUnknownDoesNotDowngradeCompleted()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var scope = factory.Services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var scenario = await SeedTeamAsync(context, TeamMemberRole.Owner, cancellationToken);
        var externalId = Guid.NewGuid().ToString("D");
        var link = AddLink(context, scenario.Team.Id, externalId, null, true);
        await context.SaveChangesAsync(cancellationToken);
        var provider = new FakeProvider(externalId);
        var match = Match(350, 35);
        match.Status = ExternalMatchStatus.Rescheduled;
        provider.SetSchedule(externalId, match);
        var sync = CreateSyncService(context, provider);

        await sync.SyncExternalLinkAsync(link.Id, cancellationToken);
        var scheduledEvent = await context.Events.SingleAsync(value => value.TeamId == scenario.Team.Id, cancellationToken);
        Assert.Equal(EventStatus.Rescheduled, scheduledEvent.Status);
        scheduledEvent.Status = EventStatus.Completed;
        await context.SaveChangesAsync(cancellationToken);
        match.Status = ExternalMatchStatus.Unknown;
        await sync.SyncExternalLinkAsync(link.Id, cancellationToken);

        context.ChangeTracker.Clear();
        Assert.Equal(EventStatus.Completed, (await context.Events.AsNoTracking().SingleAsync(value => value.Id == scheduledEvent.Id, cancellationToken)).Status);
    }

    [Fact]
    public async Task ExistingArenaAddress_IsPreservedForEmptySource_AndUpdatedForNewValue()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var scope = factory.Services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var scenario = await SeedTeamAsync(context, TeamMemberRole.Owner, cancellationToken);
        var externalId = Guid.NewGuid().ToString("D");
        var link = AddLink(context, scenario.Team.Id, externalId, null, true);
        var stored = StoredEvent(scenario.Team.Id, 360, 36);
        stored.LocationAddress = "Известный адрес";
        context.Events.Add(stored);
        await context.SaveChangesAsync(cancellationToken);
        var provider = new FakeProvider(externalId);
        var match = Match(360, 36, address: null);
        provider.SetSchedule(externalId, match);
        var sync = CreateSyncService(context, provider);

        await sync.SyncExternalLinkAsync(link.Id, cancellationToken);
        context.ChangeTracker.Clear();
        Assert.Equal("Известный адрес", (await context.Events.AsNoTracking().SingleAsync(value => value.Id == stored.Id, cancellationToken)).LocationAddress);

        match.ArenaAddress = "Новый подтверждённый адрес";
        await sync.SyncExternalLinkAsync(link.Id, cancellationToken);
        context.ChangeTracker.Clear();
        Assert.Equal("Новый подтверждённый адрес", (await context.Events.AsNoTracking().SingleAsync(value => value.Id == stored.Id, cancellationToken)).LocationAddress);
    }

    [Fact]
    public async Task SpbhlProvider_MapsExplicitRescheduledStatus()
    {
        var matches = new SpbhlScheduleHtmlParser().ParseSchedule(ReadFixture("schedule-rescheduled.html"));
        var provider = new SpbhlExternalLeagueProvider(new FixtureSpbhlClient(matches));

        var normalized = Assert.Single(await provider.GetTeamScheduleAsync(
            Guid.NewGuid().ToString("D"),
            TestContext.Current.CancellationToken));

        Assert.Equal(ExternalMatchStatus.Rescheduled, normalized.Status);
        Assert.Equal("Санкт-Петербург, Стрельна, Фронтовая ул., 3", normalized.ArenaAddress);
    }

    [Fact]
    public async Task SpbhlProvider_MapsCoverAndHumanContactLabels()
    {
        var teamId = Guid.Parse("f4286850-d18e-4e16-bbe2-a0577764a0c6");
        var parsed = new SpbhlTeamProfileHtmlParser().ParseTeamProfile(
            ReadFixture("team-profile-administrator.html"),
            teamId);
        Assert.NotNull(parsed);
        parsed.CoverUrl = "https://spbhl.ru/ImageHandlerInt.ashx?ID=5687&Size=O&TableName=TeamSeason";
        parsed.WebsiteUrls = ["https://club.example"];
        var provider = new SpbhlExternalLeagueProvider(new FixtureSpbhlClient([], parsed));

        var normalized = await provider.GetTeamProfileAsync(teamId.ToString("D"), TestContext.Current.CancellationToken);

        Assert.NotNull(normalized);
        Assert.Equal(parsed.CoverUrl, normalized.CoverUrl);
        Assert.Equal("Администратор", Assert.Single(normalized.Phones).Label);
        Assert.Equal("Сайт команды", Assert.Single(normalized.WebsiteUrls).Label);
    }

    [Fact]
    public async Task Sync_PersistsCoverFromRealSpbhlPhotoFixture()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var scope = factory.Services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var scenario = await SeedTeamAsync(context, TeamMemberRole.Owner, cancellationToken);
        var externalId = Guid.Parse("e883398e-311c-4214-8bb4-6869db4b3791");
        var link = AddLink(context, scenario.Team.Id, externalId.ToString("D"), null, true);
        await context.SaveChangesAsync(cancellationToken);
        var profile = new SpbhlTeamProfileHtmlParser().ParseTeamProfile(
            ReadFixture("team-profile-photo.html"),
            externalId);
        Assert.NotNull(profile);
        var provider = new SpbhlExternalLeagueProvider(new FixtureSpbhlClient([], profile));

        await CreateSyncService(context, provider).SyncExternalLinkAsync(link.Id, cancellationToken);

        context.ChangeTracker.Clear();
        var persisted = await context.TeamExternalLeagueLinks.AsNoTracking()
            .SingleAsync(value => value.Id == link.Id, cancellationToken);
        Assert.Equal(
            "https://spbhl.ru/ImageHandlerInt.ashx?ID=5514&Size=O&TableName=TeamSeason",
            persisted.CoverUrl);
    }

    [Fact]
    public async Task SpbhlProvider_UsesReasonablePhoneFallbackWithoutAdministrator()
    {
        var teamId = Guid.NewGuid();
        var profile = new SpbhlTeamProfile
        {
            TeamId = teamId,
            Name = "Команда",
            ProfileUrl = $"https://spbhl.ru/Team?TeamID={teamId:D}",
            Phones = ["8 (911) 139-02-69"]
        };
        var provider = new SpbhlExternalLeagueProvider(new FixtureSpbhlClient([], profile));

        var normalized = await provider.GetTeamProfileAsync(teamId.ToString("D"), TestContext.Current.CancellationToken);

        Assert.Equal("Официальный контакт", Assert.Single(normalized!.Phones).Label);
    }

    [Fact]
    public async Task Sync_RefreshesReliableProfileMetadata_WithoutClearingKnownValues()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var scope = factory.Services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var scenario = await SeedTeamAsync(context, TeamMemberRole.Owner, cancellationToken);
        var externalId = Guid.NewGuid().ToString("D");
        var link = AddLink(context, scenario.Team.Id, externalId, null, true);
        link.CoachName = "Старый тренер";
        await context.SaveChangesAsync(cancellationToken);
        var provider = new FakeProvider(externalId)
        {
            Profile = new ExternalTeamProfile
            {
                Provider = ExternalLeagueProvider.Spbhl,
                ExternalTeamId = externalId,
                Name = "Canonical team",
                FoundedYear = 2015,
                AdministratorName = "Администратор",
                CoverUrl = "https://spbhl.ru/season-photo.jpg",
                Phones = [new ExternalContactCandidate { Value = "8 (911) 139-02-69", Label = "Администратор" }],
                WebsiteUrls = [new ExternalContactCandidate { Value = "https://club.example", Label = "Сайт команды" }]
            }
        };

        await CreateSyncService(context, provider).SyncExternalLinkAsync(link.Id, cancellationToken);

        context.ChangeTracker.Clear();
        var persisted = await context.TeamExternalLeagueLinks.AsNoTracking().SingleAsync(value => value.Id == link.Id, cancellationToken);
        Assert.Equal(2015, persisted.FoundedYear);
        Assert.Equal("Старый тренер", persisted.CoachName);
        Assert.Equal("Администратор", persisted.AdministratorName);
        Assert.Equal("https://spbhl.ru/season-photo.jpg", persisted.CoverUrl);
        var phones = JsonSerializer.Deserialize<List<ExternalContactCandidate>>(persisted.PhonesJson!);
        var websites = JsonSerializer.Deserialize<List<ExternalContactCandidate>>(persisted.WebsiteUrlsJson!);
        var phone = Assert.Single(phones!);
        var website = Assert.Single(websites!);
        Assert.Equal("Администратор", phone.Label);
        Assert.Contains("139-02-69", phone.Value);
        Assert.Equal("Сайт команды", website.Label);
        Assert.Contains("club.example", website.Value);
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
    public async Task Update_PreservesHockeyPlannerOwnedFieldsAndUpdatesSourceOwnedFields()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var scope = factory.Services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var scenario = await SeedTeamAsync(context, TeamMemberRole.Owner, cancellationToken);
        var externalId = Guid.NewGuid().ToString("D");
        var link = AddLink(context, scenario.Team.Id, externalId, null, true);
        var stored = StoredEvent(scenario.Team.Id, 600, 60);
        stored.Description = "Internal description";
        var uniformColor = new UniformColor
        {
            Name = "Home black",
            ImageUrl = "https://hockeyplanner.test/uniforms/home-black.png",
            CreatedByUserId = scenario.User.Id,
            TeamId = scenario.Team.Id
        };
        stored.UniformColor = uniformColor;
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
        changed.HomeTeamName = "Updated home";
        changed.AwayTeamName = "Updated away";
        provider.SetSchedule(externalId, changed);

        await CreateSyncService(context, provider).SyncExternalLinkAsync(link.Id, cancellationToken);

        context.ChangeTracker.Clear();
        var persisted = await context.Events.AsNoTracking().SingleAsync(value => value.Id == stored.Id, cancellationToken);
        var attendance = await context.Attendances.AsNoTracking().SingleAsync(value => value.Id == attendanceId, cancellationToken);
        Assert.Equal("Internal description", persisted.Description);
        Assert.Equal(uniformColor.Id, persisted.UniformColorId);
        Assert.Equal(changed.StartTime.UtcDateTime, persisted.StartTime);
        Assert.Equal("Updated home — Updated away", persisted.Title);
        Assert.Equal("Updated home", persisted.HomeTeamName);
        Assert.Equal("Updated away", persisted.AwayTeamName);
        Assert.Equal("New arena", persisted.LocationName);
        Assert.Equal("New address", persisted.LocationAddress);
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

    private static ScheduledEvent ExternalVenueEvent(Guid teamId, string venue, string address) => new()
    {
        TeamId = teamId,
        Title = "External venue",
        Type = EventType.Game,
        StartTime = DateTime.UtcNow,
        Status = EventStatus.Scheduled,
        LocationName = venue,
        LocationAddress = address,
        ExternalLeagueProvider = ExternalLeagueProvider.Spbhl,
        ExternalCompetitionId = Guid.NewGuid().ToString("N"),
        ExternalMatchId = Guid.NewGuid().ToString("N")
    };

    private static int CountOccurrences(string value, string fragment) =>
        value.Split(fragment, StringSplitOptions.None).Length - 1;

    private static string ReadFixture(string fileName) =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "Spbhl", fileName));

    private class FakeProvider(params string[] profileIds) : IExternalLeagueProvider
    {
        private readonly Dictionary<string, IReadOnlyCollection<ExternalMatch>> _schedules = new(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _profileIds = new(profileIds, StringComparer.OrdinalIgnoreCase);
        public ExternalLeagueProvider Provider => ExternalLeagueProvider.Spbhl;
        public string? LastSearchTitle { get; private set; }
        public ExternalMatchDetails? Details { get; set; }
        public ExternalTeamProfile? Profile { get; set; }
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
            ExternalTeamProfile? result = Profile ?? (_profileIds.Contains(externalTeamId) ? new()
            {
                Provider = Provider,
                ExternalTeamId = externalTeamId,
                Name = "Canonical team",
                ProfileUrl = $"https://spbhl.ru/Team?TeamID={externalTeamId}",
                LogoUrl = "https://spbhl.ru/logo.png",
                City = "Санкт-Петербург",
                Country = "Россия"
            } : null);
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

    private sealed class FixtureSpbhlClient(
        IReadOnlyCollection<SpbhlMatchItem> matches,
        SpbhlTeamProfile? profile = null) : ISpbhlClient
    {
        public Task<IReadOnlyCollection<SpbhlTeamSearchItem>> SearchTeamsAsync(string? title, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
        public Task<IReadOnlyCollection<SpbhlMatchItem>> GetTeamScheduleAsync(Guid teamId, CancellationToken cancellationToken) =>
            Task.FromResult(matches);
        public Task<SpbhlMatchDetails?> GetMatchDetailsAsync(int tournamentId, int matchId, CancellationToken cancellationToken) =>
            Task.FromResult<SpbhlMatchDetails?>(null);
        public Task<SpbhlTeamProfile?> GetTeamProfileAsync(Guid teamId, CancellationToken cancellationToken) =>
            Task.FromResult(profile);
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
        public Task<ExternalTeamProfile?> GetTeamProfileAsync(string id, CancellationToken cancellationToken) =>
            Task.FromResult<ExternalTeamProfile?>(null);
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

    private sealed class ConcurrentProfileScheduleProvider(string externalTeamId) : IExternalLeagueProvider
    {
        private readonly TaskCompletionSource _profileStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _scheduleStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ExternalLeagueProvider Provider => ExternalLeagueProvider.Spbhl;
        public bool ProfileStarted => _profileStarted.Task.IsCompleted;
        public bool ScheduleStarted => _scheduleStarted.Task.IsCompleted;
        public Task<IReadOnlyCollection<ExternalTeamSearchItem>> SearchTeamsAsync(string title, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
        public async Task<ExternalTeamProfile?> GetTeamProfileAsync(string id, CancellationToken cancellationToken)
        {
            Assert.Equal(externalTeamId, id);
            _profileStarted.TrySetResult();
            await _scheduleStarted.Task.WaitAsync(cancellationToken);
            return null;
        }
        public async Task<IReadOnlyCollection<ExternalMatch>> GetTeamScheduleAsync(string id, CancellationToken cancellationToken)
        {
            Assert.Equal(externalTeamId, id);
            _scheduleStarted.TrySetResult();
            await _profileStarted.Task.WaitAsync(cancellationToken);
            return [Match(221, 1)];
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
