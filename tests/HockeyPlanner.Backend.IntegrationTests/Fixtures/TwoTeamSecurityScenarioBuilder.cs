using HockeyPlanner.Backend.Core.Entities;
using HockeyPlanner.Backend.Core.Enums;
using HockeyPlanner.Backend.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace HockeyPlanner.Backend.IntegrationTests.Fixtures;

public sealed record TwoTeamSecurityScenario(
    User UserA,
    User UserB,
    Team TeamA,
    Team TeamB,
    ScheduledEvent EventB,
    Attendance AttendanceB,
    Line LineB,
    Player PlayerB);

public static class TwoTeamSecurityScenarioBuilder
{
    public static async Task<TwoTeamSecurityScenario> CreateAsync(
        IServiceProvider services,
        CancellationToken cancellationToken = default)
    {
        var suffix = Guid.NewGuid().ToString("N");
        var now = DateTime.UtcNow;

        var userA = new User
        {
            FirstName = "Owner",
            LastName = "Alpha",
            Email = $"owner-alpha-{suffix}@test.invalid",
            EmailConfirmed = true,
            Role = UserRole.Player,
            AppRole = AppRole.User,
        };
        var userB = new User
        {
            FirstName = "Owner",
            LastName = "Bravo",
            Email = $"owner-bravo-{suffix}@test.invalid",
            EmailConfirmed = true,
            Role = UserRole.Player,
            AppRole = AppRole.User,
        };

        var teamA = new Team
        {
            Name = $"Alpha {suffix}",
            Visibility = TeamVisibility.Private,
            InviteCode = $"A{suffix[..10]}",
            CreatedByUserId = userA.Id,
        };
        var teamB = new Team
        {
            Name = $"Bravo {suffix}",
            Visibility = TeamVisibility.Private,
            InviteCode = $"B{suffix[..10]}",
            CreatedByUserId = userB.Id,
        };

        var membershipA = new TeamMembership
        {
            TeamId = teamA.Id,
            UserId = userA.Id,
            Role = TeamMemberRole.Owner,
        };
        var membershipB = new TeamMembership
        {
            TeamId = teamB.Id,
            UserId = userB.Id,
            Role = TeamMemberRole.Owner,
        };

        var eventB = CreateEvent("Bravo practice", teamB.Id, now.AddDays(1));
        var attendanceB = CreateAttendance(eventB.Id, userB.Id, now);
        var lineB = CreateLine("Bravo line", eventB.Id);
        var playerB = CreatePlayer(lineB.Id, userB);

        await using var scope = services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        await dbContext.AddRangeAsync(
            new object[]
            {
                userA,
                userB,
                teamA,
                teamB,
                membershipA,
                membershipB,
                eventB,
                attendanceB,
                lineB,
                playerB,
            },
            cancellationToken);
        await dbContext.SaveChangesAsync(cancellationToken);

        dbContext.ChangeTracker.Clear();

        return new TwoTeamSecurityScenario(
            userA,
            userB,
            teamA,
            teamB,
            eventB,
            attendanceB,
            lineB,
            playerB);
    }

    private static ScheduledEvent CreateEvent(string title, Guid teamId, DateTime startTime) =>
        new()
        {
            Title = title,
            Type = EventType.Practice,
            StartTime = startTime,
            DurationMinutes = 75,
            Status = EventStatus.Scheduled,
            LocationName = "Test rink",
            LocationAddress = "Test address",
            TeamId = teamId,
        };

    private static Attendance CreateAttendance(Guid eventId, Guid userId, DateTime now) =>
        new()
        {
            EventId = eventId,
            UserId = userId,
            Status = AttendanceStatus.Confirmed,
            RespondedAt = now,
        };

    private static Line CreateLine(string name, Guid eventId) =>
        new()
        {
            Name = name,
            Order = 1,
            EventId = eventId,
        };

    private static Player CreatePlayer(Guid lineId, User user) =>
        new()
        {
            LineId = lineId,
            UserId = user.Id,
            FirstName = user.FirstName,
            LastName = user.LastName,
            Role = PlayerRole.Center,
        };
}
