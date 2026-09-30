using HockeyPlanner.Backend.Core.Entities;
using HockeyPlanner.Backend.Core.Enums;
using HockeyPlanner.Backend.Infrastructure.Data;
using Microsoft.Extensions.DependencyInjection;

namespace HockeyPlanner.Backend.IntegrationTests.Fixtures;

public sealed record TeamApiBaselineScenario(TwoTeamSecurityScenario Pair, User Admin, User Member,
    TeamNews NewsA, TeamNews NewsB, TeamTable TableA, TeamTable TableB,
    ScheduledEvent EventA, EventTableProtocol ProtocolA, EventTableProtocol ProtocolB);

public static class TeamApiBaselineScenarioBuilder
{
    public static async Task<TeamApiBaselineScenario> CreateAsync(IServiceProvider services, bool publicB = false)
    {
        var ct = TestContext.Current.CancellationToken;
        var pair = await TwoTeamSecurityScenarioBuilder.CreateAsync(services, ct);
        var admin = new User { FirstName = "Admin", LastName = "Baseline", EmailConfirmed = true };
        var member = new User { FirstName = "Member", LastName = "Baseline", EmailConfirmed = true };
        var newsA = new TeamNews { TeamId = pair.TeamA.Id, AuthorUserId = pair.UserA.Id, Title = "News A", Body = "Private A" };
        var newsB = new TeamNews { TeamId = pair.TeamB.Id, AuthorUserId = pair.UserB.Id, Title = "News B", Body = "Private B" };
        var tableA = new TeamTable { TeamId = pair.TeamA.Id, CreatedByUserId = pair.UserA.Id, Name = "Table A", TemplateType = TeamTableTemplateType.PlayerStats };
        var tableB = new TeamTable { TeamId = pair.TeamB.Id, CreatedByUserId = pair.UserB.Id, Name = "Table B", TemplateType = TeamTableTemplateType.PlayerStats };
        var eventA = new ScheduledEvent { TeamId = pair.TeamA.Id, Title = "Event A", StartTime = pair.EventB.StartTime, DurationMinutes = 60, LocationName = "Test rink", LocationAddress = "Test address" };
        var protocolA = new EventTableProtocol { EventId = eventA.Id, TeamTableId = tableA.Id, CreatedByUserId = pair.UserA.Id };
        var protocolB = new EventTableProtocol { EventId = pair.EventB.Id, TeamTableId = tableB.Id, CreatedByUserId = pair.UserB.Id };
        protocolA.Rows.Add(new EventTableProtocolRow { UserId = pair.UserA.Id });
        protocolB.Rows.Add(new EventTableProtocolRow { UserId = pair.UserB.Id });
        tableA.Rows.Add(new TeamTableRow { UserId = pair.UserA.Id });
        foreach (var user in new[] { pair.UserB, admin, member }) tableB.Rows.Add(new TeamTableRow { UserId = user.Id });
        await using var scope = services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var teamB = await db.Teams.FindAsync([pair.TeamB.Id], ct);
        teamB!.Visibility = publicB ? TeamVisibility.Public : TeamVisibility.Private;
        // Existing builder generates lowercase suffixes; join-by-code uppercases input.
        teamB.InviteCode = teamB.InviteCode.ToUpperInvariant();
        pair.TeamB.InviteCode = teamB.InviteCode;
        db.AddRange(admin, member, newsA, newsB, tableA, tableB, eventA, protocolA, protocolB,
            new TeamMembership { TeamId = teamB.Id, UserId = admin.Id, Role = TeamMemberRole.Admin },
            new TeamMembership { TeamId = teamB.Id, UserId = member.Id, Role = TeamMemberRole.Member });
        await db.SaveChangesAsync(ct);
        return new(pair, admin, member, newsA, newsB, tableA, tableB, eventA, protocolA, protocolB);
    }
}
