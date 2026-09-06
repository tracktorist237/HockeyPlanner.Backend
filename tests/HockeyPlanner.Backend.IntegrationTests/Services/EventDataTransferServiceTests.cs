using HockeyPlanner.Backend.Application.Abstractions.Services;
using HockeyPlanner.Backend.Core.Entities;
using HockeyPlanner.Backend.Core.Enums;
using HockeyPlanner.Backend.Core.Exceptions;
using HockeyPlanner.Backend.Infrastructure.Data;
using HockeyPlanner.Backend.IntegrationTests.Infrastructure;
using HockeyPlanner.Backend.WebAPI.Models.Events;
using HockeyPlanner.Backend.WebAPI.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace HockeyPlanner.Backend.IntegrationTests.Services;

[Collection(IntegrationTestCollection.Name)]
public sealed class EventDataTransferServiceTests(HockeyPlannerWebApplicationFactory factory)
{
    public static TheoryData<AttendanceTransferMode, AttendanceStatus, AttendanceStatus?, AttendanceStatus?, bool> AttendanceModeCases => new()
    {
        { AttendanceTransferMode.ReplaceTarget, AttendanceStatus.Confirmed, AttendanceStatus.Confirmed, AttendanceStatus.Confirmed, false },
        { AttendanceTransferMode.ReplaceTarget, AttendanceStatus.Confirmed, AttendanceStatus.Declined, AttendanceStatus.Confirmed, true },
        { AttendanceTransferMode.ReplaceTarget, AttendanceStatus.Confirmed, AttendanceStatus.Pending, AttendanceStatus.Confirmed, true },
        { AttendanceTransferMode.ReplaceTarget, AttendanceStatus.Confirmed, null, AttendanceStatus.Confirmed, true },
        { AttendanceTransferMode.ReplaceTarget, AttendanceStatus.Declined, AttendanceStatus.Confirmed, AttendanceStatus.Declined, true },
        { AttendanceTransferMode.ReplaceTarget, AttendanceStatus.Declined, AttendanceStatus.Declined, AttendanceStatus.Declined, false },
        { AttendanceTransferMode.ReplaceTarget, AttendanceStatus.Declined, AttendanceStatus.Pending, AttendanceStatus.Declined, true },
        { AttendanceTransferMode.ReplaceTarget, AttendanceStatus.Declined, null, AttendanceStatus.Declined, true },
        { AttendanceTransferMode.ReplaceTarget, AttendanceStatus.Pending, AttendanceStatus.Confirmed, AttendanceStatus.Confirmed, false },
        { AttendanceTransferMode.ReplaceTarget, AttendanceStatus.Pending, AttendanceStatus.Declined, AttendanceStatus.Declined, false },
        { AttendanceTransferMode.ReplaceTarget, AttendanceStatus.Pending, AttendanceStatus.Pending, AttendanceStatus.Pending, false },
        { AttendanceTransferMode.ReplaceTarget, AttendanceStatus.Pending, null, AttendanceStatus.Pending, true },

        { AttendanceTransferMode.MergePreferTarget, AttendanceStatus.Confirmed, AttendanceStatus.Confirmed, AttendanceStatus.Confirmed, false },
        { AttendanceTransferMode.MergePreferTarget, AttendanceStatus.Confirmed, AttendanceStatus.Declined, AttendanceStatus.Declined, false },
        { AttendanceTransferMode.MergePreferTarget, AttendanceStatus.Confirmed, AttendanceStatus.Pending, AttendanceStatus.Confirmed, true },
        { AttendanceTransferMode.MergePreferTarget, AttendanceStatus.Confirmed, null, AttendanceStatus.Confirmed, true },
        { AttendanceTransferMode.MergePreferTarget, AttendanceStatus.Declined, AttendanceStatus.Confirmed, AttendanceStatus.Confirmed, false },
        { AttendanceTransferMode.MergePreferTarget, AttendanceStatus.Declined, AttendanceStatus.Declined, AttendanceStatus.Declined, false },
        { AttendanceTransferMode.MergePreferTarget, AttendanceStatus.Declined, AttendanceStatus.Pending, AttendanceStatus.Declined, true },
        { AttendanceTransferMode.MergePreferTarget, AttendanceStatus.Declined, null, AttendanceStatus.Declined, true },
        { AttendanceTransferMode.MergePreferTarget, AttendanceStatus.Pending, AttendanceStatus.Confirmed, AttendanceStatus.Confirmed, false },
        { AttendanceTransferMode.MergePreferTarget, AttendanceStatus.Pending, AttendanceStatus.Declined, AttendanceStatus.Declined, false },
        { AttendanceTransferMode.MergePreferTarget, AttendanceStatus.Pending, AttendanceStatus.Pending, AttendanceStatus.Pending, false },
        { AttendanceTransferMode.MergePreferTarget, AttendanceStatus.Pending, null, AttendanceStatus.Pending, true },

        { AttendanceTransferMode.ConfirmedOnly, AttendanceStatus.Confirmed, AttendanceStatus.Confirmed, AttendanceStatus.Confirmed, false },
        { AttendanceTransferMode.ConfirmedOnly, AttendanceStatus.Confirmed, AttendanceStatus.Declined, AttendanceStatus.Confirmed, true },
        { AttendanceTransferMode.ConfirmedOnly, AttendanceStatus.Confirmed, AttendanceStatus.Pending, AttendanceStatus.Confirmed, true },
        { AttendanceTransferMode.ConfirmedOnly, AttendanceStatus.Confirmed, null, AttendanceStatus.Confirmed, true },
        { AttendanceTransferMode.ConfirmedOnly, AttendanceStatus.Declined, AttendanceStatus.Confirmed, AttendanceStatus.Confirmed, false },
        { AttendanceTransferMode.ConfirmedOnly, AttendanceStatus.Declined, AttendanceStatus.Declined, AttendanceStatus.Declined, false },
        { AttendanceTransferMode.ConfirmedOnly, AttendanceStatus.Declined, AttendanceStatus.Pending, AttendanceStatus.Pending, false },
        { AttendanceTransferMode.ConfirmedOnly, AttendanceStatus.Declined, null, null, false },
        { AttendanceTransferMode.ConfirmedOnly, AttendanceStatus.Pending, AttendanceStatus.Confirmed, AttendanceStatus.Confirmed, false },
        { AttendanceTransferMode.ConfirmedOnly, AttendanceStatus.Pending, AttendanceStatus.Declined, AttendanceStatus.Declined, false },
        { AttendanceTransferMode.ConfirmedOnly, AttendanceStatus.Pending, AttendanceStatus.Pending, AttendanceStatus.Pending, false },
        { AttendanceTransferMode.ConfirmedOnly, AttendanceStatus.Pending, null, null, false }
    };

    [Fact]
    public async Task Transfer_MergesSelectedData_PreservesTargetMatchData_AndDeletesSource()
    {
        var token = TestContext.Current.CancellationToken;
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var (owner, team) = await SeedTeamAsync(db, token);
        var color = new UniformColor { Name = "Black", ImageUrl = "https://test/black.png", TeamId = team.Id, CreatedByUserId = owner.Id };
        var source = Event(team.Id, "Manual", false);
        source.Description = "User description";
        source.UniformColor = color;
        source.Attendances.Add(new Attendance { UserId = owner.Id, Status = AttendanceStatus.Confirmed });
        source.EventGuests.Add(new EventGuest { InvitedByUserId = owner.Id, FirstName = "Guest", LastName = "One", Status = AttendanceStatus.Confirmed });
        source.Roster.Add(new Line { Name = "First", Order = 1, Players = [new Player { UserId = owner.Id, FirstName = "Owner", LastName = "User", Role = PlayerRole.Center }] });
        var target = Event(team.Id, "League title", true);
        target.Attendances.Add(new Attendance { UserId = owner.Id, Status = AttendanceStatus.Pending });
        db.AddRange(source, target);
        await db.SaveChangesAsync(token);
        var targetTime = target.StartTime;
        var targetExternalId = target.ExternalMatchId;
        db.ChangeTracker.Clear();

        await new EventDataTransferService(db, new RecordingTransferNotificationService()).TransferAsync(source.Id, owner.Id, new TransferEventDataRequest
        {
            TargetEventId = target.Id, Attendance = true, Roster = true, Guests = true,
            UniformColor = true, Description = true, DeleteSourceEvent = true
        }, token);

        db.ChangeTracker.Clear();
        var saved = await db.Events.Include(value => value.Attendances).Include(value => value.EventGuests)
            .Include(value => value.Roster).ThenInclude(value => value.Players).SingleAsync(value => value.Id == target.Id, token);
        Assert.Equal("League title", saved.Title);
        Assert.Equal(targetTime, saved.StartTime, TimeSpan.FromMilliseconds(1));
        Assert.Equal(targetExternalId, saved.ExternalMatchId);
        Assert.Equal("User description", saved.Description);
        Assert.Equal(color.Id, saved.UniformColorId);
        Assert.Equal(AttendanceStatus.Confirmed, Assert.Single(saved.Attendances).Status);
        Assert.Single(saved.EventGuests);
        Assert.Single(Assert.Single(saved.Roster).Players);
        Assert.False(await db.Events.AnyAsync(value => value.Id == source.Id, token));
    }

    [Fact]
    public async Task Transfer_TargetExplicitAttendanceWins_AndRosterIsReplacedWithoutGuestDuplicates()
    {
        var token = TestContext.Current.CancellationToken;
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var (owner, team) = await SeedTeamAsync(db, token);
        var source = Event(team.Id, "Source", false);
        source.Attendances.Add(new Attendance { UserId = owner.Id, Status = AttendanceStatus.Confirmed });
        source.EventGuests.Add(new EventGuest { InvitedByUserId = owner.Id, FirstName = "Same", LastName = "Guest" });
        source.Roster.Add(new Line { Name = "Source line", Players = [new Player { UserId = owner.Id, FirstName = "A", LastName = "B" }] });
        var target = Event(team.Id, "Target", true);
        target.Attendances.Add(new Attendance { UserId = owner.Id, Status = AttendanceStatus.Declined });
        target.EventGuests.Add(new EventGuest { InvitedByUserId = owner.Id, FirstName = "same", LastName = "guest" });
        target.Roster.Add(new Line { Name = "Old target line" });
        db.AddRange(source, target);
        await db.SaveChangesAsync(token);
        db.ChangeTracker.Clear();

        await new EventDataTransferService(db, new RecordingTransferNotificationService()).TransferAsync(source.Id, owner.Id, new TransferEventDataRequest
        { TargetEventId = target.Id, Attendance = true, Guests = true, Roster = true }, token);

        db.ChangeTracker.Clear();
        var saved = await db.Events.Include(value => value.Attendances).Include(value => value.EventGuests)
            .Include(value => value.Roster).SingleAsync(value => value.Id == target.Id, token);
        Assert.Equal(AttendanceStatus.Declined, Assert.Single(saved.Attendances).Status);
        Assert.Single(saved.EventGuests);
        Assert.Equal("Source line", Assert.Single(saved.Roster).Name);
    }

    [Fact]
    public async Task Transfer_RejectsSameEventDifferentTeamsAndUnauthorizedUser()
    {
        var token = TestContext.Current.CancellationToken;
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var (owner, firstTeam) = await SeedTeamAsync(db, token);
        var (_, secondTeam) = await SeedTeamAsync(db, token);
        var source = Event(firstTeam.Id, "Source", false);
        var target = Event(secondTeam.Id, "Target", true);
        db.AddRange(source, target);
        await db.SaveChangesAsync(token);
        db.ChangeTracker.Clear();
        var service = new EventDataTransferService(db, new RecordingTransferNotificationService());

        await Assert.ThrowsAsync<BusinessRuleException>(() => service.TransferAsync(source.Id, owner.Id, new() { TargetEventId = source.Id }, token));
        await Assert.ThrowsAsync<BusinessRuleException>(() => service.TransferAsync(source.Id, owner.Id, new() { TargetEventId = target.Id }, token));
        var trackedTarget = db.Events.Local.Single(value => value.Id == target.Id);
        trackedTarget.TeamId = firstTeam.Id;
        await db.SaveChangesAsync(token);
        await Assert.ThrowsAsync<UnauthorizedException>(() => service.TransferAsync(source.Id, Guid.NewGuid(), new() { TargetEventId = target.Id }, token));
    }

    [Fact]
    public async Task Transfer_ConfirmedAttendance_NotifiesEachAffectedUserOnce_WithTargetEventLink()
    {
        var token = TestContext.Current.CancellationToken;
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var (owner, team) = await SeedTeamAsync(db, token);
        var secondUser = new User { FirstName = "Second", LastName = "User", Role = UserRole.Player, AppRole = AppRole.User };
        var source = Event(team.Id, "Source", false);
        source.Attendances.Add(new Attendance { UserId = owner.Id, Status = AttendanceStatus.Confirmed });
        source.Attendances.Add(new Attendance { UserId = secondUser.Id, Status = AttendanceStatus.Confirmed });
        var target = Event(team.Id, "Target league event", true);
        target.Attendances.Add(new Attendance { UserId = owner.Id, Status = AttendanceStatus.Pending });
        db.AddRange(secondUser, source, target);
        await db.SaveChangesAsync(token);
        db.ChangeTracker.Clear();
        var notifications = new RecordingTransferNotificationService();

        await new EventDataTransferService(db, notifications).TransferAsync(source.Id, owner.Id, new TransferEventDataRequest
        { TargetEventId = target.Id, Attendance = true }, token);

        Assert.Equal(2, notifications.Calls.Count);
        Assert.True(notifications.Calls.Select(value => value.UserId).ToHashSet().SetEquals([owner.Id, secondUser.Id]));
        Assert.All(notifications.Calls, call =>
        {
            Assert.Equal(NotificationType.EventPublished, call.Type);
            Assert.Equal(NotificationCategory.AttendanceRequired, call.Category);
            Assert.Equal("Явка перенесена", call.Title);
            Assert.Equal("Ваша отметка «Смогу» перенесена в мероприятие «Target league event».", call.Body);
            Assert.Equal($"/events/{target.Id}", call.Url);
        });
    }

    [Fact]
    public async Task Transfer_TargetExplicitResponseAndNonConfirmedSource_DoNotNotify()
    {
        var token = TestContext.Current.CancellationToken;
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var (owner, team) = await SeedTeamAsync(db, token);
        var pendingUser = new User { FirstName = "Pending", LastName = "User", Role = UserRole.Player, AppRole = AppRole.User };
        var declinedUser = new User { FirstName = "Declined", LastName = "User", Role = UserRole.Player, AppRole = AppRole.User };
        var source = Event(team.Id, "Source", false);
        source.Attendances.Add(new Attendance { UserId = owner.Id, Status = AttendanceStatus.Confirmed });
        source.Attendances.Add(new Attendance { UserId = pendingUser.Id, Status = AttendanceStatus.Pending });
        source.Attendances.Add(new Attendance { UserId = declinedUser.Id, Status = AttendanceStatus.Declined });
        var target = Event(team.Id, "Target", true);
        target.Attendances.Add(new Attendance { UserId = owner.Id, Status = AttendanceStatus.Declined });
        db.AddRange(pendingUser, declinedUser, source, target);
        await db.SaveChangesAsync(token);
        db.ChangeTracker.Clear();
        var notifications = new RecordingTransferNotificationService();

        await new EventDataTransferService(db, notifications).TransferAsync(source.Id, owner.Id, new TransferEventDataRequest
        { TargetEventId = target.Id, Attendance = true }, token);

        Assert.Empty(notifications.Calls);
    }

    [Theory]
    [MemberData(nameof(AttendanceModeCases))]
    public async Task AttendancePreviewAndTransfer_FollowSelectedMode(
        AttendanceTransferMode mode,
        AttendanceStatus sourceStatus,
        AttendanceStatus? targetStatus,
        AttendanceStatus? expectedStatus,
        bool expectedChange)
    {
        var token = TestContext.Current.CancellationToken;
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var (owner, team) = await SeedTeamAsync(db, token);
        var source = Event(team.Id, "Source", false);
        source.Attendances.Add(new Attendance { UserId = owner.Id, Status = sourceStatus });
        var target = Event(team.Id, "Target", false);
        if (targetStatus.HasValue)
            target.Attendances.Add(new Attendance { UserId = owner.Id, Status = targetStatus.Value });
        db.AddRange(source, target);
        await db.SaveChangesAsync(token);
        db.ChangeTracker.Clear();
        var notifications = new RecordingTransferNotificationService();
        var service = new EventDataTransferService(db, notifications);

        var preview = await service.PreviewAttendanceAsync(source.Id, owner.Id, new PreviewAttendanceTransferRequest
        { TargetEventId = target.Id, AttendanceTransferMode = mode }, token);

        var item = Assert.Single(preview.Items);
        Assert.Equal(owner.Id, item.UserId);
        Assert.Equal("User Owner", item.UserDisplayName);
        Assert.Equal(sourceStatus, item.SourceStatus);
        Assert.Equal(targetStatus, item.TargetStatus);
        Assert.Equal(expectedStatus, item.ResultingStatus);
        Assert.Equal(expectedChange, item.WillChange);
        Assert.Equal(expectedChange ? 1 : 0, preview.ChangedCount);

        db.ChangeTracker.Clear();
        await service.TransferAsync(source.Id, owner.Id, new TransferEventDataRequest
        { TargetEventId = target.Id, Attendance = true, AttendanceTransferMode = mode }, token);

        db.ChangeTracker.Clear();
        var savedStatus = await db.Attendances.Where(value => value.EventId == target.Id && value.UserId == owner.Id)
            .Select(value => (AttendanceStatus?)value.Status).SingleOrDefaultAsync(token);
        Assert.Equal(expectedStatus, savedStatus);
        Assert.Equal(expectedChange && expectedStatus == AttendanceStatus.Confirmed ? 1 : 0, notifications.Calls.Count);
    }

    [Theory]
    [InlineData(AttendanceTransferMode.MergePreferTarget, 0)]
    [InlineData(AttendanceTransferMode.ReplaceTarget, 1)]
    public async Task Transfer_RosterFiltersPlayersByResultingAttendance(
        AttendanceTransferMode mode,
        int expectedPlayers)
    {
        var token = TestContext.Current.CancellationToken;
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var (owner, team) = await SeedTeamAsync(db, token);
        var source = Event(team.Id, "Source", false);
        source.Attendances.Add(new Attendance { UserId = owner.Id, Status = AttendanceStatus.Confirmed });
        source.Roster.Add(new Line { Name = "First", Order = 1, Players = [new Player { UserId = owner.Id, FirstName = "Owner", LastName = "User" }] });
        var target = Event(team.Id, "Target", true);
        target.Attendances.Add(new Attendance { UserId = owner.Id, Status = AttendanceStatus.Declined });
        db.AddRange(source, target);
        await db.SaveChangesAsync(token);
        db.ChangeTracker.Clear();

        await new EventDataTransferService(db, new RecordingTransferNotificationService()).TransferAsync(source.Id, owner.Id, new TransferEventDataRequest
        { TargetEventId = target.Id, Attendance = true, Roster = true, AttendanceTransferMode = mode }, token);

        db.ChangeTracker.Clear();
        var saved = await db.Events.Include(value => value.Attendances).Include(value => value.Roster).ThenInclude(value => value.Players)
            .SingleAsync(value => value.Id == target.Id, token);
        Assert.Equal(mode == AttendanceTransferMode.ReplaceTarget ? AttendanceStatus.Confirmed : AttendanceStatus.Declined, Assert.Single(saved.Attendances).Status);
        Assert.Equal(expectedPlayers, Assert.Single(saved.Roster).Players.Count);
    }

    [Fact]
    public async Task Transfer_DoesNotAutoPlaceConfirmedParticipantMissingFromSourceRoster()
    {
        var token = TestContext.Current.CancellationToken;
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var (owner, team) = await SeedTeamAsync(db, token);
        var source = Event(team.Id, "Source", false);
        source.Attendances.Add(new Attendance { UserId = owner.Id, Status = AttendanceStatus.Confirmed });
        source.Roster.Add(new Line { Name = "Empty source line", Order = 1 });
        var target = Event(team.Id, "Target", true);
        target.Attendances.Add(new Attendance { UserId = owner.Id, Status = AttendanceStatus.Pending });
        db.AddRange(source, target);
        await db.SaveChangesAsync(token);
        db.ChangeTracker.Clear();

        await new EventDataTransferService(db, new RecordingTransferNotificationService()).TransferAsync(source.Id, owner.Id, new TransferEventDataRequest
        { TargetEventId = target.Id, Attendance = true, Roster = true }, token);

        db.ChangeTracker.Clear();
        var line = await db.Lines.Include(value => value.Players).SingleAsync(value => value.EventId == target.Id, token);
        Assert.Empty(line.Players);
    }

    [Theory]
    [InlineData(AttendanceTransferMode.MergePreferTarget, 0)]
    [InlineData(AttendanceTransferMode.ReplaceTarget, 1)]
    public async Task Transfer_GuestRosterPlayerFollowsResultingAttendance(
        AttendanceTransferMode mode,
        int expectedPlayers)
    {
        var token = TestContext.Current.CancellationToken;
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var (owner, team) = await SeedTeamAsync(db, token);
        var sourceGuest = new EventGuest { InvitedByUserId = owner.Id, FirstName = "Guest", LastName = "Player", Status = AttendanceStatus.Confirmed };
        var source = Event(team.Id, "Source", false);
        source.EventGuests.Add(sourceGuest);
        source.Roster.Add(new Line { Name = "Guests", Order = 1, Players = [new Player { EventGuestId = sourceGuest.Id, FirstName = "Guest", LastName = "Player" }] });
        var target = Event(team.Id, "Target", true);
        target.EventGuests.Add(new EventGuest { InvitedByUserId = owner.Id, FirstName = "Guest", LastName = "Player", Status = AttendanceStatus.Declined });
        db.AddRange(source, target);
        await db.SaveChangesAsync(token);
        db.ChangeTracker.Clear();

        await new EventDataTransferService(db, new RecordingTransferNotificationService()).TransferAsync(source.Id, owner.Id, new TransferEventDataRequest
        { TargetEventId = target.Id, Attendance = true, Roster = true, AttendanceTransferMode = mode }, token);

        db.ChangeTracker.Clear();
        var saved = await db.Events.Include(value => value.EventGuests).Include(value => value.Roster).ThenInclude(value => value.Players)
            .SingleAsync(value => value.Id == target.Id, token);
        Assert.Equal(mode == AttendanceTransferMode.ReplaceTarget ? AttendanceStatus.Confirmed : AttendanceStatus.Declined, Assert.Single(saved.EventGuests).Status);
        Assert.Equal(expectedPlayers, Assert.Single(saved.Roster).Players.Count);
    }

    [Fact]
    public async Task Transfer_FilteringPreservesLineOrderAndPartialEmptyLinesWithoutRebalancing()
    {
        var token = TestContext.Current.CancellationToken;
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var (owner, team) = await SeedTeamAsync(db, token);
        var declinedUser = new User { FirstName = "Declined", LastName = "Player", Role = UserRole.Player, AppRole = AppRole.User };
        var source = Event(team.Id, "Source", false);
        source.Attendances.Add(new Attendance { UserId = owner.Id, Status = AttendanceStatus.Confirmed });
        source.Attendances.Add(new Attendance { UserId = declinedUser.Id, Status = AttendanceStatus.Declined });
        source.Roster.Add(new Line { Name = "First", Order = 1, Players = [
            new Player { UserId = owner.Id, FirstName = "Owner", LastName = "User" },
            new Player { UserId = declinedUser.Id, FirstName = "Declined", LastName = "Player" }
        ] });
        source.Roster.Add(new Line { Name = "Second", Order = 2, Players = [new Player { UserId = declinedUser.Id, FirstName = "Declined", LastName = "Player" }] });
        var target = Event(team.Id, "Target", true);
        db.AddRange(declinedUser, source, target);
        await db.SaveChangesAsync(token);
        db.ChangeTracker.Clear();

        await new EventDataTransferService(db, new RecordingTransferNotificationService()).TransferAsync(source.Id, owner.Id, new TransferEventDataRequest
        { TargetEventId = target.Id, Attendance = true, Roster = true }, token);

        db.ChangeTracker.Clear();
        var lines = await db.Lines.Include(value => value.Players).Where(value => value.EventId == target.Id).OrderBy(value => value.Order).ToListAsync(token);
        Assert.Equal(["First", "Second"], lines.Select(value => value.Name));
        Assert.Single(lines[0].Players);
        Assert.Equal(owner.Id, lines[0].Players.Single().UserId);
        Assert.Empty(lines[1].Players);
    }

    [Fact]
    public async Task Transfer_RosterWithoutAttendancePreservesAllSourcePlayers()
    {
        var token = TestContext.Current.CancellationToken;
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var (owner, team) = await SeedTeamAsync(db, token);
        var source = Event(team.Id, "Source", false);
        source.Roster.Add(new Line { Name = "First", Players = [new Player { UserId = owner.Id, FirstName = "Owner", LastName = "User" }] });
        var target = Event(team.Id, "Target", true);
        target.Attendances.Add(new Attendance { UserId = owner.Id, Status = AttendanceStatus.Declined });
        db.AddRange(source, target);
        await db.SaveChangesAsync(token);
        db.ChangeTracker.Clear();

        await new EventDataTransferService(db, new RecordingTransferNotificationService()).TransferAsync(source.Id, owner.Id, new TransferEventDataRequest
        { TargetEventId = target.Id, Attendance = false, Roster = true }, token);

        Assert.Single(await db.Players.Where(value => value.Line.EventId == target.Id).ToListAsync(token));
    }

    private static ScheduledEvent Event(Guid teamId, string title, bool external) => new()
    {
        TeamId = teamId, Title = title, Type = EventType.Game, Status = EventStatus.Scheduled,
        StartTime = DateTime.UtcNow.AddDays(1), DurationMinutes = 75, LocationName = "Arena", LocationAddress = "Address",
        ExternalLeagueProvider = external ? ExternalLeagueProvider.Spbhl : null,
        ExternalCompetitionId = external ? "competition" : null, ExternalMatchId = external ? "match" : null
    };

    private static async Task<(User User, Team Team)> SeedTeamAsync(AppDbContext db, CancellationToken token)
    {
        var user = new User { FirstName = "Owner", LastName = "User", Role = UserRole.Player, AppRole = AppRole.User };
        var team = new Team { Name = $"Team {Guid.NewGuid():N}", InviteCode = Guid.NewGuid().ToString("N")[..20], Visibility = TeamVisibility.Private, CreatedByUserId = user.Id };
        db.AddRange(user, team);
        db.TeamMemberships.Add(new TeamMembership { TeamId = team.Id, UserId = user.Id, Role = TeamMemberRole.Owner });
        await db.SaveChangesAsync(token);
        return (user, team);
    }
}

internal sealed class RecordingTransferNotificationService : INotificationService
{
    public List<(Guid UserId, NotificationType Type, NotificationCategory Category, string Title, string Body, string? Url)> Calls { get; } = [];

    public Task NotifyUserAsync(Guid userId, NotificationType type, NotificationCategory category, string title, string body, string? url = null, CancellationToken cancellationToken = default)
    {
        Calls.Add((userId, type, category, title, body, url));
        return Task.CompletedTask;
    }

    public Task NotifyUsersAsync(IReadOnlyCollection<Guid> userIds, NotificationType type, NotificationCategory category, string title, string body, string? url = null, CancellationToken cancellationToken = default) =>
        Task.WhenAll(userIds.Select(userId => NotifyUserAsync(userId, type, category, title, body, url, cancellationToken)));

    public Task NotifyTeamAsync(Guid teamId, NotificationType type, NotificationCategory category, string title, string body, string? url = null, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();
}
