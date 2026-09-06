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

        await new EventDataTransferService(db).TransferAsync(source.Id, owner.Id, new TransferEventDataRequest
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

        await new EventDataTransferService(db).TransferAsync(source.Id, owner.Id, new TransferEventDataRequest
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
        var service = new EventDataTransferService(db);

        await Assert.ThrowsAsync<BusinessRuleException>(() => service.TransferAsync(source.Id, owner.Id, new() { TargetEventId = source.Id }, token));
        await Assert.ThrowsAsync<BusinessRuleException>(() => service.TransferAsync(source.Id, owner.Id, new() { TargetEventId = target.Id }, token));
        var trackedTarget = db.Events.Local.Single(value => value.Id == target.Id);
        trackedTarget.TeamId = firstTeam.Id;
        await db.SaveChangesAsync(token);
        await Assert.ThrowsAsync<UnauthorizedException>(() => service.TransferAsync(source.Id, Guid.NewGuid(), new() { TargetEventId = target.Id }, token));
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
