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
[Trait("Category", "SpbhlTeamSync")]
public sealed class SpbhlTeamSyncServiceTests(HockeyPlannerWebApplicationFactory factory)
{
    [Fact]
    public async Task UnlinkedTeam_ThrowsBusinessRule_WithoutCallingClient()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var scope = factory.Services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var team = await SeedTeamAsync(context, linked: false, cancellationToken: cancellationToken);
        var client = new FakeSpbhlClient();
        var service = CreateService(context, client);

        await Assert.ThrowsAsync<BusinessRuleException>(() => service.SyncTeamAsync(team.Id, cancellationToken));

        Assert.Equal(0, client.ScheduleCallCount);
    }

    [Fact]
    public async Task MissingTeam_ThrowsNotFound_WithoutCallingClient()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var scope = factory.Services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var client = new FakeSpbhlClient();
        var service = CreateService(context, client);

        await Assert.ThrowsAsync<NotFoundException>(() => service.SyncTeamAsync(Guid.NewGuid(), cancellationToken));

        Assert.Equal(0, client.ScheduleCallCount);
    }

    [Fact]
    public async Task HttpFailure_PersistsAttempt_AndLeavesSuccessAndEventsUnchanged()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var scope = factory.Services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var team = await SeedTeamAsync(context, successfulSyncAt: new DateTime(2026, 8, 1, 10, 0, 0, DateTimeKind.Utc), cancellationToken: cancellationToken);
        var scheduledEvent = CreateStoredImportedEvent(team.Id, FutureMatch());
        scheduledEvent.Description = "Must survive";
        context.Events.Add(scheduledEvent);
        await context.SaveChangesAsync(cancellationToken);
        var previousSuccessfulAt = team.SpbhlLastSuccessfulSyncAt;
        var previousStartTime = scheduledEvent.StartTime;
        var beforeAttempt = DateTime.UtcNow;
        var client = new FakeSpbhlClient(new HttpRequestException("SPbHL unavailable"));
        var service = CreateService(context, client);

        await Assert.ThrowsAsync<HttpRequestException>(() => service.SyncTeamAsync(team.Id, cancellationToken));

        context.ChangeTracker.Clear();
        var persistedTeam = await context.Teams.AsNoTracking().SingleAsync(value => value.Id == team.Id, cancellationToken);
        var persistedEvent = await context.Events.AsNoTracking().SingleAsync(value => value.Id == scheduledEvent.Id, cancellationToken);
        Assert.NotNull(persistedTeam.SpbhlLastSyncAttemptAt);
        Assert.True(persistedTeam.SpbhlLastSyncAttemptAt >= beforeAttempt);
        Assert.Equal(previousSuccessfulAt, persistedTeam.SpbhlLastSuccessfulSyncAt);
        Assert.Equal(previousStartTime, persistedEvent.StartTime);
        Assert.Equal("Must survive", persistedEvent.Description);
    }

    [Fact]
    public async Task LinkIdentityChangedDuringHttp_RejectsStaleScheduleWithoutUpdatingSuccess()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var syncScope = factory.Services.CreateAsyncScope();
        var syncContext = syncScope.ServiceProvider.GetRequiredService<AppDbContext>();
        var team = await SeedTeamAsync(syncContext, cancellationToken: cancellationToken);
        var expectedSpbhlTeamId = team.SpbhlTeamId!.Value;
        var client = new BlockingSpbhlClient(new SpbhlMatchItem[] { FutureMatch() });
        var syncTask = CreateService(syncContext, client).SyncTeamAsync(team.Id, cancellationToken);
        await client.RequestStarted.WaitAsync(cancellationToken);

        var replacementSpbhlTeamId = Guid.NewGuid();
        await using (var mutationScope = factory.Services.CreateAsyncScope())
        {
            var mutationContext = mutationScope.ServiceProvider.GetRequiredService<AppDbContext>();
            var currentTeam = await mutationContext.Teams.SingleAsync(value => value.Id == team.Id, cancellationToken);
            currentTeam.SpbhlTeamId = replacementSpbhlTeamId;
            currentTeam.SpbhlTeamName = "Replacement team";
            await mutationContext.SaveChangesAsync(cancellationToken);
        }

        client.Complete();
        var exception = await Assert.ThrowsAsync<BusinessRuleException>(() => syncTask);

        Assert.Equal("Привязка команды СПбХЛ изменилась во время синхронизации.", exception.Message);
        await using var assertionScope = factory.Services.CreateAsyncScope();
        var assertionContext = assertionScope.ServiceProvider.GetRequiredService<AppDbContext>();
        var persistedTeam = await assertionContext.Teams.AsNoTracking().SingleAsync(value => value.Id == team.Id, cancellationToken);
        Assert.NotEqual(expectedSpbhlTeamId, persistedTeam.SpbhlTeamId);
        Assert.Equal(replacementSpbhlTeamId, persistedTeam.SpbhlTeamId);
        Assert.NotNull(persistedTeam.SpbhlLastSyncAttemptAt);
        Assert.Null(persistedTeam.SpbhlLastSuccessfulSyncAt);
        Assert.False(await assertionContext.Events.AnyAsync(value => value.TeamId == team.Id, cancellationToken));
    }

    [Fact]
    public async Task UnbindDuringHttp_DoesNotImportStaleMatches()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var syncScope = factory.Services.CreateAsyncScope();
        var syncContext = syncScope.ServiceProvider.GetRequiredService<AppDbContext>();
        var team = await SeedTeamAsync(syncContext, cancellationToken: cancellationToken);
        var client = new BlockingSpbhlClient(new SpbhlMatchItem[] { FutureMatch() });
        var syncTask = CreateService(syncContext, client).SyncTeamAsync(team.Id, cancellationToken);
        await client.RequestStarted.WaitAsync(cancellationToken);

        await using (var mutationScope = factory.Services.CreateAsyncScope())
        {
            var mutationContext = mutationScope.ServiceProvider.GetRequiredService<AppDbContext>();
            var currentTeam = await mutationContext.Teams.SingleAsync(value => value.Id == team.Id, cancellationToken);
            currentTeam.SpbhlTeamId = null;
            currentTeam.SpbhlTeamName = null;
            currentTeam.SpbhlLastSyncAttemptAt = null;
            currentTeam.SpbhlLastSuccessfulSyncAt = null;
            await mutationContext.SaveChangesAsync(cancellationToken);
        }

        client.Complete();
        await Assert.ThrowsAsync<BusinessRuleException>(() => syncTask);

        await using var assertionScope = factory.Services.CreateAsyncScope();
        var assertionContext = assertionScope.ServiceProvider.GetRequiredService<AppDbContext>();
        var persistedTeam = await assertionContext.Teams.AsNoTracking().SingleAsync(value => value.Id == team.Id, cancellationToken);
        Assert.Null(persistedTeam.SpbhlTeamId);
        Assert.Null(persistedTeam.SpbhlLastSuccessfulSyncAt);
        Assert.False(await assertionContext.Events.AnyAsync(value => value.TeamId == team.Id, cancellationToken));
    }

    [Fact]
    public async Task RebindDuringHttp_DoesNotImportPreviousProfileSchedule()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var syncScope = factory.Services.CreateAsyncScope();
        var syncContext = syncScope.ServiceProvider.GetRequiredService<AppDbContext>();
        var team = await SeedTeamAsync(syncContext, cancellationToken: cancellationToken);
        var originalSpbhlTeamId = team.SpbhlTeamId!.Value;
        var client = new BlockingSpbhlClient(new SpbhlMatchItem[] { FutureMatch() });
        var syncTask = CreateService(syncContext, client).SyncTeamAsync(team.Id, cancellationToken);
        await client.RequestStarted.WaitAsync(cancellationToken);

        var reboundSpbhlTeamId = Guid.NewGuid();
        await using (var mutationScope = factory.Services.CreateAsyncScope())
        {
            var mutationContext = mutationScope.ServiceProvider.GetRequiredService<AppDbContext>();
            var currentTeam = await mutationContext.Teams.SingleAsync(value => value.Id == team.Id, cancellationToken);
            currentTeam.SpbhlTeamId = reboundSpbhlTeamId;
            currentTeam.SpbhlTeamName = "Rebound profile";
            await mutationContext.SaveChangesAsync(cancellationToken);
        }

        client.Complete();
        await Assert.ThrowsAsync<BusinessRuleException>(() => syncTask);

        await using var assertionScope = factory.Services.CreateAsyncScope();
        var assertionContext = assertionScope.ServiceProvider.GetRequiredService<AppDbContext>();
        var persistedTeam = await assertionContext.Teams.AsNoTracking().SingleAsync(value => value.Id == team.Id, cancellationToken);
        Assert.Equal(reboundSpbhlTeamId, persistedTeam.SpbhlTeamId);
        Assert.NotEqual(originalSpbhlTeamId, persistedTeam.SpbhlTeamId);
        Assert.Null(persistedTeam.SpbhlLastSuccessfulSyncAt);
        Assert.False(await assertionContext.Events.AnyAsync(value => value.TeamId == team.Id, cancellationToken));
    }

    [Fact]
    public async Task InitialFutureMatch_CreatesExpectedScheduledEvent()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var scope = factory.Services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var team = await SeedTeamAsync(context, cancellationToken: cancellationToken);
        var match = FutureMatch();
        var service = CreateService(context, new FakeSpbhlClient(new SpbhlMatchItem[] { match }));

        var result = await service.SyncTeamAsync(team.Id, cancellationToken);

        context.ChangeTracker.Clear();
        var scheduledEvent = await context.Events.AsNoTracking()
            .SingleAsync(value => value.TeamId == team.Id, cancellationToken);
        Assert.Equal(1, result.ReceivedCount);
        Assert.Equal(1, result.CreatedCount);
        Assert.Equal(0, result.UpdatedCount);
        Assert.Equal("Ладога — АЛГА", scheduledEvent.Title);
        Assert.Equal(EventType.Game, scheduledEvent.Type);
        Assert.Equal(match.StartTime.UtcDateTime, scheduledEvent.StartTime);
        Assert.Equal(75, scheduledEvent.DurationMinutes);
        Assert.Equal(EventStatus.Scheduled, scheduledEvent.Status);
        Assert.Equal("АХФ Арена", scheduledEvent.LocationName);
        Assert.Equal(string.Empty, scheduledEvent.LocationAddress);
        Assert.Equal("Ладога", scheduledEvent.HomeTeamName);
        Assert.Equal("АЛГА", scheduledEvent.AwayTeamName);
        Assert.Equal(team.Id, scheduledEvent.TeamId);
        Assert.Equal(6537, scheduledEvent.SpbhlTournamentId);
        Assert.Equal(118664, scheduledEvent.SpbhlMatchId);
        Assert.Equal(match.MatchUrl, scheduledEvent.SpbhlMatchUrl);
        Assert.NotNull(scheduledEvent.SpbhlLastSyncedAt);
        Assert.Null(scheduledEvent.HomeScore);
        Assert.Null(scheduledEvent.AwayScore);
        Assert.Null(scheduledEvent.LeagueName);
        Assert.Null(scheduledEvent.UniformColorId);
    }

    [Fact]
    public async Task NewMatch_CreatesOnePendingAttendancePerMembership_AndRepeatIsIdempotent()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var scope = factory.Services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var team = await SeedTeamAsync(context, memberCount: 3, cancellationToken: cancellationToken);
        var match = FutureMatch();
        var client = new FakeSpbhlClient(
            new SpbhlMatchItem[] { match },
            new SpbhlMatchItem[] { match });
        var service = CreateService(context, client);

        var first = await service.SyncTeamAsync(team.Id, cancellationToken);
        var eventId = await context.Events.Where(value => value.TeamId == team.Id).Select(value => value.Id).SingleAsync(cancellationToken);
        var second = await service.SyncTeamAsync(team.Id, cancellationToken);

        context.ChangeTracker.Clear();
        var events = await context.Events.AsNoTracking().Where(value => value.TeamId == team.Id).ToArrayAsync(cancellationToken);
        var attendances = await context.Attendances.AsNoTracking().Where(value => value.EventId == eventId).ToArrayAsync(cancellationToken);
        var memberIds = await context.TeamMemberships.AsNoTracking().Where(value => value.TeamId == team.Id).Select(value => value.UserId).ToArrayAsync(cancellationToken);
        Assert.Equal(1, first.CreatedCount);
        Assert.Equal(0, second.CreatedCount);
        Assert.Equal(0, second.UpdatedCount);
        Assert.Equal(1, second.UnchangedCount);
        Assert.Single(events);
        Assert.Equal(eventId, events[0].Id);
        Assert.Equal(3, attendances.Length);
        Assert.All(attendances, attendance => Assert.Equal(AttendanceStatus.Pending, attendance.Status));
        Assert.Equal(memberIds.Order(), attendances.Select(value => value.UserId).Order());
        Assert.All(attendances, attendance => Assert.Equal(events[0].CreatedAt, attendance.CreatedAt));
        Assert.All(attendances, attendance => Assert.Equal(events[0].CreatedAt, attendance.RespondedAt));
    }

    [Fact]
    public async Task FinishedMatch_CreatesCompletedEventWithScore()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var scope = factory.Services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var team = await SeedTeamAsync(context, cancellationToken: cancellationToken);
        var service = CreateService(context, new FakeSpbhlClient(new SpbhlMatchItem[] { FinishedMatch() }));

        await service.SyncTeamAsync(team.Id, cancellationToken);

        var scheduledEvent = await context.Events.AsNoTracking().SingleAsync(value => value.TeamId == team.Id, cancellationToken);
        Assert.Equal(EventStatus.Completed, scheduledEvent.Status);
        Assert.Equal(4, scheduledEvent.HomeScore);
        Assert.Equal(2, scheduledEvent.AwayScore);
    }

    [Fact]
    public async Task RescheduledMatch_UpdatesSameEventAndArena()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var scope = factory.Services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var team = await SeedTeamAsync(context, cancellationToken: cancellationToken);
        var firstMatch = FutureMatch();
        firstMatch.ArenaName = "Arena A";
        var movedMatch = FutureMatch();
        movedMatch.StartTime = new DateTimeOffset(2026, 9, 8, 21, 30, 0, TimeSpan.FromHours(3));
        movedMatch.ArenaName = "Arena B";
        var service = CreateService(context, new FakeSpbhlClient(
            new SpbhlMatchItem[] { firstMatch },
            new SpbhlMatchItem[] { movedMatch }));

        await service.SyncTeamAsync(team.Id, cancellationToken);
        var eventId = await context.Events.Where(value => value.TeamId == team.Id).Select(value => value.Id).SingleAsync(cancellationToken);
        var second = await service.SyncTeamAsync(team.Id, cancellationToken);

        context.ChangeTracker.Clear();
        var scheduledEvent = await context.Events.AsNoTracking().SingleAsync(value => value.Id == eventId, cancellationToken);
        Assert.Equal(1, second.UpdatedCount);
        Assert.Equal(eventId, scheduledEvent.Id);
        Assert.Equal(movedMatch.StartTime.UtcDateTime, scheduledEvent.StartTime);
        Assert.Equal("Arena B", scheduledEvent.LocationName);
        Assert.NotEqual(EventStatus.Rescheduled, scheduledEvent.Status);
    }

    [Fact]
    public async Task ExistingMatchUpdate_PreservesDescriptionAttendanceAndGuest()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var scope = factory.Services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var team = await SeedTeamAsync(context, memberCount: 1, cancellationToken: cancellationToken);
        var initial = FutureMatch();
        var updated = FutureMatch();
        updated.HomeTeamName = "Ладога обновлённая";
        updated.ArenaName = "Новая арена";
        var service = CreateService(context, new FakeSpbhlClient(
            new SpbhlMatchItem[] { initial },
            new SpbhlMatchItem[] { updated }));
        await service.SyncTeamAsync(team.Id, cancellationToken);
        var scheduledEvent = await context.Events.Include(value => value.Attendances)
            .SingleAsync(value => value.TeamId == team.Id, cancellationToken);
        var attendance = Assert.Single(scheduledEvent.Attendances);
        var attendanceId = attendance.Id;
        attendance.Status = AttendanceStatus.Confirmed;
        attendance.Notes = "Confirmed internally";
        scheduledEvent.Description = "Internal description";
        scheduledEvent.LocationAddress = "Internal address";
        var guest = new EventGuest
        {
            EventId = scheduledEvent.Id,
            InvitedByUserId = attendance.UserId,
            FirstName = "Guest",
            LastName = "Player",
            Status = AttendanceStatus.Confirmed
        };
        context.EventGuests.Add(guest);
        await context.SaveChangesAsync(cancellationToken);

        await service.SyncTeamAsync(team.Id, cancellationToken);

        context.ChangeTracker.Clear();
        var persisted = await context.Events.AsNoTracking()
            .Include(value => value.Attendances)
            .Include(value => value.EventGuests)
            .SingleAsync(value => value.Id == scheduledEvent.Id, cancellationToken);
        var persistedAttendance = Assert.Single(persisted.Attendances);
        Assert.Equal("Ладога обновлённая — АЛГА", persisted.Title);
        Assert.Equal("Новая арена", persisted.LocationName);
        Assert.Equal("Internal description", persisted.Description);
        Assert.Equal("Internal address", persisted.LocationAddress);
        Assert.Equal(attendanceId, persistedAttendance.Id);
        Assert.Equal(AttendanceStatus.Confirmed, persistedAttendance.Status);
        Assert.Equal("Confirmed internally", persistedAttendance.Notes);
        Assert.Equal(guest.Id, Assert.Single(persisted.EventGuests).Id);
    }

    [Fact]
    public async Task FinishedUpdate_CompletesSameEventAndAddsScore()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var scope = factory.Services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var team = await SeedTeamAsync(context, cancellationToken: cancellationToken);
        var initial = FinishedMatch();
        initial.Status = SpbhlMatchStatus.Unknown;
        initial.HomeScore = null;
        initial.AwayScore = null;
        var finished = FinishedMatch();
        var service = CreateService(context, new FakeSpbhlClient(
            new SpbhlMatchItem[] { initial },
            new SpbhlMatchItem[] { finished }));

        await service.SyncTeamAsync(team.Id, cancellationToken);
        var eventId = await context.Events.Where(value => value.TeamId == team.Id).Select(value => value.Id).SingleAsync(cancellationToken);
        await service.SyncTeamAsync(team.Id, cancellationToken);

        context.ChangeTracker.Clear();
        var scheduledEvent = await context.Events.AsNoTracking().SingleAsync(value => value.Id == eventId, cancellationToken);
        Assert.Equal(eventId, scheduledEvent.Id);
        Assert.Equal(EventStatus.Completed, scheduledEvent.Status);
        Assert.Equal(4, scheduledEvent.HomeScore);
        Assert.Equal(2, scheduledEvent.AwayScore);
    }

    [Fact]
    public async Task UnknownUpdate_DoesNotDowngradeCompletedOrClearScoreOrArena()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var scope = factory.Services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var team = await SeedTeamAsync(context, cancellationToken: cancellationToken);
        var finished = FinishedMatch();
        finished.ArenaName = "Known arena";
        var unknown = FinishedMatch();
        unknown.Status = SpbhlMatchStatus.Unknown;
        unknown.HomeScore = null;
        unknown.AwayScore = null;
        unknown.ArenaName = null;
        var service = CreateService(context, new FakeSpbhlClient(
            new SpbhlMatchItem[] { finished },
            new SpbhlMatchItem[] { unknown }));

        await service.SyncTeamAsync(team.Id, cancellationToken);
        var second = await service.SyncTeamAsync(team.Id, cancellationToken);

        context.ChangeTracker.Clear();
        var scheduledEvent = await context.Events.AsNoTracking().SingleAsync(value => value.TeamId == team.Id, cancellationToken);
        Assert.Equal(0, second.UpdatedCount);
        Assert.Equal(1, second.UnchangedCount);
        Assert.Equal(EventStatus.Completed, scheduledEvent.Status);
        Assert.Equal(4, scheduledEvent.HomeScore);
        Assert.Equal(2, scheduledEvent.AwayScore);
        Assert.Equal("Known arena", scheduledEvent.LocationName);
    }

    [Fact]
    public async Task DuplicateInput_CreatesOneEvent()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var scope = factory.Services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var team = await SeedTeamAsync(context, cancellationToken: cancellationToken);
        var match = FutureMatch();
        var service = CreateService(context, new FakeSpbhlClient(new SpbhlMatchItem[] { match, match }));

        var result = await service.SyncTeamAsync(team.Id, cancellationToken);

        Assert.Equal(2, result.ReceivedCount);
        Assert.Equal(1, result.CreatedCount);
        Assert.Equal(1, await context.Events.CountAsync(value => value.TeamId == team.Id, cancellationToken));
    }

    [Fact]
    public async Task MatchingManualEvent_IsNotClaimedOrChanged()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var scope = factory.Services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var team = await SeedTeamAsync(context, cancellationToken: cancellationToken);
        var match = FutureMatch();
        var manualEvent = new ScheduledEvent
        {
            Title = "Ладога — АЛГА",
            Type = EventType.Game,
            StartTime = match.StartTime.UtcDateTime,
            DurationMinutes = 90,
            Status = EventStatus.Scheduled,
            LocationName = "Manual arena",
            LocationAddress = "Manual address",
            HomeTeamName = match.HomeTeamName,
            AwayTeamName = match.AwayTeamName,
            TeamId = team.Id,
            Description = "Manual event"
        };
        context.Events.Add(manualEvent);
        await context.SaveChangesAsync(cancellationToken);
        var service = CreateService(context, new FakeSpbhlClient(new SpbhlMatchItem[] { match }));

        await service.SyncTeamAsync(team.Id, cancellationToken);

        context.ChangeTracker.Clear();
        var events = await context.Events.AsNoTracking().Where(value => value.TeamId == team.Id).OrderBy(value => value.Id).ToArrayAsync(cancellationToken);
        Assert.Equal(2, events.Length);
        var persistedManual = Assert.Single(events, value => value.Id == manualEvent.Id);
        Assert.Null(persistedManual.SpbhlTournamentId);
        Assert.Null(persistedManual.SpbhlMatchId);
        Assert.Equal("Manual event", persistedManual.Description);
        Assert.Equal("Manual arena", persistedManual.LocationName);
        Assert.Contains(events, value => value.Id != manualEvent.Id && value.SpbhlMatchId == match.MatchId);
    }

    [Fact]
    public async Task EmptySuccessfulSchedule_LeavesOldMatchAndAdvancesSuccessTimestamps()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var scope = factory.Services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var team = await SeedTeamAsync(context, cancellationToken: cancellationToken);
        var service = CreateService(context, new FakeSpbhlClient(
            new SpbhlMatchItem[] { FutureMatch() },
            Array.Empty<SpbhlMatchItem>()));

        await service.SyncTeamAsync(team.Id, cancellationToken);
        var eventId = await context.Events.Where(value => value.TeamId == team.Id).Select(value => value.Id).SingleAsync(cancellationToken);
        var result = await service.SyncTeamAsync(team.Id, cancellationToken);

        context.ChangeTracker.Clear();
        var persistedTeam = await context.Teams.AsNoTracking().SingleAsync(value => value.Id == team.Id, cancellationToken);
        Assert.Equal(0, result.ReceivedCount);
        Assert.Equal(0, result.CreatedCount);
        Assert.Equal(0, result.UpdatedCount);
        Assert.Equal(0, result.UnchangedCount);
        Assert.True(await context.Events.AnyAsync(value => value.Id == eventId, cancellationToken));
        Assert.NotNull(persistedTeam.SpbhlLastSyncAttemptAt);
        Assert.NotNull(persistedTeam.SpbhlLastSuccessfulSyncAt);
        Assert.True(persistedTeam.SpbhlLastSuccessfulSyncAt >= persistedTeam.SpbhlLastSyncAttemptAt);
        Assert.InRange(
            (persistedTeam.SpbhlLastSuccessfulSyncAt!.Value - result.SyncedAt).Duration(),
            TimeSpan.Zero,
            TimeSpan.FromMilliseconds(1));
    }

    private static SpbhlTeamSyncService CreateService(AppDbContext context, ISpbhlClient client)
    {
        return new SpbhlTeamSyncService(context, client, NullLogger<SpbhlTeamSyncService>.Instance);
    }

    private static async Task<Team> SeedTeamAsync(
        AppDbContext context,
        bool linked = true,
        int memberCount = 0,
        DateTime? successfulSyncAt = null,
        CancellationToken cancellationToken = default)
    {
        var team = new Team
        {
            Name = $"SPbHL sync test {Guid.NewGuid():N}",
            InviteCode = Guid.NewGuid().ToString("N")[..20],
            Visibility = TeamVisibility.Private,
            CreatedByUserId = Guid.NewGuid(),
            SpbhlTeamId = linked ? Guid.NewGuid() : null,
            SpbhlTeamName = linked ? "Linked external team" : null,
            SpbhlLastSuccessfulSyncAt = successfulSyncAt
        };
        context.Teams.Add(team);

        for (var index = 0; index < memberCount; index++)
        {
            var user = new User
            {
                FirstName = $"Member{index}",
                LastName = "SyncTest",
                Role = UserRole.Player,
                AppRole = AppRole.User
            };
            context.Users.Add(user);
            context.TeamMemberships.Add(new TeamMembership
            {
                Team = team,
                User = user,
                Role = TeamMemberRole.Member
            });
        }

        await context.SaveChangesAsync(cancellationToken);
        return team;
    }

    private static ScheduledEvent CreateStoredImportedEvent(Guid teamId, SpbhlMatchItem match)
    {
        return new ScheduledEvent
        {
            Title = $"{match.HomeTeamName} — {match.AwayTeamName}",
            Type = EventType.Game,
            StartTime = match.StartTime.UtcDateTime,
            DurationMinutes = 75,
            Status = EventStatus.Scheduled,
            LocationName = match.ArenaName ?? string.Empty,
            LocationAddress = string.Empty,
            HomeTeamName = match.HomeTeamName,
            AwayTeamName = match.AwayTeamName,
            TeamId = teamId,
            SpbhlTournamentId = match.TournamentId,
            SpbhlMatchId = match.MatchId,
            SpbhlMatchUrl = match.MatchUrl,
            SpbhlLastSyncedAt = DateTime.UtcNow
        };
    }

    private static SpbhlMatchItem FutureMatch()
    {
        return new SpbhlMatchItem
        {
            MatchId = 118664,
            TournamentId = 6537,
            StartTime = new DateTimeOffset(2026, 9, 6, 19, 0, 0, TimeSpan.FromHours(3)),
            HomeTeamName = "Ладога",
            AwayTeamName = "АЛГА",
            ArenaName = "АХФ Арена",
            Status = SpbhlMatchStatus.Scheduled,
            MatchUrl = "https://spbhl.ru/Match.aspx?TournamentID=6537&MatchID=118664"
        };
    }

    private static SpbhlMatchItem FinishedMatch()
    {
        return new SpbhlMatchItem
        {
            MatchId = 118101,
            TournamentId = 6537,
            StartTime = new DateTimeOffset(2026, 7, 14, 19, 45, 0, TimeSpan.FromHours(3)),
            HomeTeamName = "Ладога",
            AwayTeamName = "Хоккейное Королевство",
            ArenaName = "Гранд Каньон Айс",
            HomeScore = 4,
            AwayScore = 2,
            Status = SpbhlMatchStatus.Finished,
            MatchUrl = "https://spbhl.ru/Match.aspx?TournamentID=6537&MatchID=118101"
        };
    }

    private sealed class FakeSpbhlClient : ISpbhlClient
    {
        private readonly Queue<IReadOnlyCollection<SpbhlMatchItem>> _scheduleResults;
        private readonly Exception? _exception;

        public FakeSpbhlClient(params IReadOnlyCollection<SpbhlMatchItem>[] scheduleResults)
        {
            _scheduleResults = new Queue<IReadOnlyCollection<SpbhlMatchItem>>(scheduleResults);
        }

        public FakeSpbhlClient(Exception exception)
        {
            _scheduleResults = new Queue<IReadOnlyCollection<SpbhlMatchItem>>();
            _exception = exception;
        }

        public int ScheduleCallCount { get; private set; }

        public Task<IReadOnlyCollection<SpbhlTeamSearchItem>> SearchTeamsAsync(
            string? title,
            CancellationToken cancellationToken)
        {
            throw new NotSupportedException();
        }

        public Task<IReadOnlyCollection<SpbhlMatchItem>> GetTeamScheduleAsync(
            Guid teamId,
            CancellationToken cancellationToken)
        {
            ScheduleCallCount++;
            if (_exception is not null)
            {
                return Task.FromException<IReadOnlyCollection<SpbhlMatchItem>>(_exception);
            }

            if (_scheduleResults.Count == 0)
            {
                return Task.FromResult<IReadOnlyCollection<SpbhlMatchItem>>([]);
            }

            return Task.FromResult(_scheduleResults.Dequeue());
        }

        public Task<SpbhlMatchDetails?> GetMatchDetailsAsync(int tournamentId, int matchId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<SpbhlTeamProfile?> GetTeamProfileAsync(Guid teamId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private sealed class BlockingSpbhlClient(IReadOnlyCollection<SpbhlMatchItem> result) : ISpbhlClient
    {
        private readonly TaskCompletionSource _requestStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<IReadOnlyCollection<SpbhlMatchItem>> _response =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task RequestStarted => _requestStarted.Task;

        public void Complete() => _response.TrySetResult(result);

        public Task<IReadOnlyCollection<SpbhlTeamSearchItem>> SearchTeamsAsync(
            string? title,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public async Task<IReadOnlyCollection<SpbhlMatchItem>> GetTeamScheduleAsync(
            Guid teamId,
            CancellationToken cancellationToken)
        {
            _requestStarted.TrySetResult();
            return await _response.Task.WaitAsync(cancellationToken);
        }

        public Task<SpbhlMatchDetails?> GetMatchDetailsAsync(int tournamentId, int matchId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<SpbhlTeamProfile?> GetTeamProfileAsync(Guid teamId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }
}
