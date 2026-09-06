using HockeyPlanner.Backend.Application.Abstractions.Services;
using HockeyPlanner.Backend.Core.Entities;
using HockeyPlanner.Backend.Core.Enums;
using HockeyPlanner.Backend.Infrastructure.Data;
using HockeyPlanner.Backend.IntegrationTests.Infrastructure;
using HockeyPlanner.Backend.Shared.Models.Events;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace HockeyPlanner.Backend.IntegrationTests.Services;

[Collection(IntegrationTestCollection.Name)]
public sealed class EventConflictServiceTests(HockeyPlannerWebApplicationFactory factory)
{
    private static readonly DateTime BaseTime = new(2026, 9, 10, 18, 0, 0, DateTimeKind.Utc);

    [Fact]
    public async Task TeamConflicts_UsePostEventBufferExclusiveBoundary_AndIgnoreCancelledEvents()
    {
        var token = TestContext.Current.CancellationToken;
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var (user, team) = await SeedTeamAsync(db, token);
        var current = Event(team.Id, "Current", BaseTime, 60);
        var normalOverlap = Event(team.Id, "Normal", BaseTime.AddMinutes(30), 30);
        var bufferOnly = Event(team.Id, "Buffer", BaseTime.AddMinutes(90), 15);
        var oneSecondBefore = Event(team.Id, "One second", BaseTime.AddHours(2).AddSeconds(-1), 15);
        var exactBoundary = Event(team.Id, "Boundary", BaseTime.AddHours(2), 15);
        var cancelled = Event(team.Id, "Cancelled", BaseTime.AddMinutes(10), 30, EventStatus.Cancelled);
        db.Events.AddRange(current, normalOverlap, bufferOnly, oneSecondBefore, exactBoundary, cancelled);
        await db.SaveChangesAsync(token);
        db.ChangeTracker.Clear();

        var conflicts = await scope.ServiceProvider.GetRequiredService<IEventConflictService>()
            .GetTeamConflictsAsync([team.Id], token);

        Assert.Contains(normalOverlap.Id, conflicts[current.Id].Select(value => value.Id));
        Assert.Contains(bufferOnly.Id, conflicts[current.Id].Select(value => value.Id));
        Assert.Contains(oneSecondBefore.Id, conflicts[current.Id].Select(value => value.Id));
        Assert.DoesNotContain(exactBoundary.Id, conflicts[current.Id].Select(value => value.Id));
        Assert.DoesNotContain(cancelled.Id, conflicts[current.Id].Select(value => value.Id));
        Assert.True(conflicts[current.Id].Count >= 3);

        var eventList = await scope.ServiceProvider.GetRequiredService<IEventService>().GetAllEvents(user.Id, team.Id, token);
        var currentDto = eventList.Events!.Single(value => value.Id == current.Id);
        Assert.Equal(conflicts[current.Id].Select(value => value.Id), currentDto.Conflicts.Select(value => value.Id));
    }

    [Fact]
    public async Task PersonalConflicts_FindConfirmedAttendanceAcrossTeams_AndIgnoreOtherStatuses()
    {
        var token = TestContext.Current.CancellationToken;
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var (user, firstTeam) = await SeedTeamAsync(db, token);
        var secondTeam = await AddTeamAsync(db, user, token);
        var current = Event(firstTeam.Id, "Current", BaseTime, 60);
        var confirmed = Event(secondTeam.Id, "Other team", BaseTime.AddMinutes(90), 30);
        var pending = Event(secondTeam.Id, "Pending", BaseTime.AddMinutes(20), 30);
        var declined = Event(secondTeam.Id, "Declined", BaseTime.AddMinutes(40), 30);
        var cancelled = Event(secondTeam.Id, "Cancelled", BaseTime.AddMinutes(30), 30, EventStatus.Cancelled);
        db.Events.AddRange(current, confirmed, pending, declined, cancelled);
        db.Attendances.AddRange(
            Attendance(user.Id, confirmed.Id, AttendanceStatus.Confirmed),
            Attendance(user.Id, pending.Id, AttendanceStatus.Pending),
            Attendance(user.Id, declined.Id, AttendanceStatus.Declined),
            Attendance(user.Id, cancelled.Id, AttendanceStatus.Confirmed));
        await db.SaveChangesAsync(token);
        db.ChangeTracker.Clear();

        var result = await scope.ServiceProvider.GetRequiredService<IEventConflictService>()
            .GetPersonalConflictsAsync(user.Id, current.Id, token);

        var conflict = Assert.Single(result);
        Assert.Equal(confirmed.Id, conflict.Id);
        Assert.Equal(secondTeam.Name, conflict.TeamName);
    }

    [Fact]
    public async Task AttendanceConfirmation_RequiresOverride_ButChangingAwayNeverWarns()
    {
        var token = TestContext.Current.CancellationToken;
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var (user, team) = await SeedTeamAsync(db, token);
        var otherTeam = await AddTeamAsync(db, user, token);
        var current = Event(team.Id, "Current", BaseTime, 60);
        var other = Event(otherTeam.Id, "Other", BaseTime.AddMinutes(90), 30);
        db.Events.AddRange(current, other);
        db.Attendances.AddRange(
            Attendance(user.Id, current.Id, AttendanceStatus.Pending),
            Attendance(user.Id, other.Id, AttendanceStatus.Confirmed));
        await db.SaveChangesAsync(token);
        db.ChangeTracker.Clear();
        var service = scope.ServiceProvider.GetRequiredService<IEventService>();

        var warning = await service.UpdateAttendance(current.Id, user.Id,
            new UpdateAttendanceRequest { Status = AttendanceStatus.Confirmed }, user.Id, token);
        Assert.Single(warning);
        Assert.Equal(AttendanceStatus.Pending, await db.Attendances.AsNoTracking()
            .Where(value => value.EventId == current.Id && value.UserId == user.Id).Select(value => value.Status).SingleAsync(token));

        var confirmed = await service.UpdateAttendance(current.Id, user.Id,
            new UpdateAttendanceRequest { Status = AttendanceStatus.Confirmed, IgnoreConflicts = true }, user.Id, token);
        Assert.Empty(confirmed);
        var away = await service.UpdateAttendance(current.Id, user.Id,
            new UpdateAttendanceRequest { Status = AttendanceStatus.Declined }, user.Id, token);
        Assert.Empty(away);
    }

    private static ScheduledEvent Event(Guid teamId, string title, DateTime start, int duration, EventStatus status = EventStatus.Scheduled) => new()
    {
        TeamId = teamId, Title = title, Type = EventType.Game, Status = status, StartTime = start,
        DurationMinutes = duration, LocationName = string.Empty, LocationAddress = string.Empty
    };

    private static Attendance Attendance(Guid userId, Guid eventId, AttendanceStatus status) => new()
    {
        UserId = userId, EventId = eventId, Status = status, RespondedAt = BaseTime
    };

    private static async Task<(User User, Team Team)> SeedTeamAsync(AppDbContext db, CancellationToken token)
    {
        var user = new User { FirstName = "Conflict", LastName = "User", Role = UserRole.Player, AppRole = AppRole.User };
        var team = new Team { Name = $"Team {Guid.NewGuid():N}", InviteCode = Guid.NewGuid().ToString("N")[..20], Visibility = TeamVisibility.Private, CreatedByUserId = user.Id };
        db.AddRange(user, team);
        db.TeamMemberships.Add(new TeamMembership { TeamId = team.Id, UserId = user.Id, Role = TeamMemberRole.Owner });
        await db.SaveChangesAsync(token);
        return (user, team);
    }

    private static async Task<Team> AddTeamAsync(AppDbContext db, User user, CancellationToken token)
    {
        var team = new Team { Name = $"Team {Guid.NewGuid():N}", InviteCode = Guid.NewGuid().ToString("N")[..20], Visibility = TeamVisibility.Private, CreatedByUserId = user.Id };
        db.Teams.Add(team);
        db.TeamMemberships.Add(new TeamMembership { TeamId = team.Id, UserId = user.Id, Role = TeamMemberRole.Member });
        await db.SaveChangesAsync(token);
        return team;
    }
}
