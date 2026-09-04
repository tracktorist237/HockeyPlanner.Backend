using HockeyPlanner.Backend.Core.Entities;
using HockeyPlanner.Backend.Core.Enums;
using HockeyPlanner.Backend.Infrastructure.Data;
using HockeyPlanner.Backend.IntegrationTests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace HockeyPlanner.Backend.IntegrationTests.Services;

[Collection(IntegrationTestCollection.Name)]
[Trait("Category", "SpbhlPersistence")]
public sealed class SpbhlPersistenceTests(HockeyPlannerWebApplicationFactory factory)
{
    [Fact]
    public async Task Teams_WithNullExternalIdentity_CanCoexist_AndSnapshotDoesNotReplaceLocalName()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var scope = factory.Services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var first = CreateTeam("Local team one");
        var second = CreateTeam("Local team two");
        first.SpbhlTeamName = "External snapshot";

        dbContext.Teams.AddRange(first, second);
        await dbContext.SaveChangesAsync(cancellationToken);
        dbContext.ChangeTracker.Clear();

        var persisted = await dbContext.Teams.AsNoTracking()
            .Where(team => team.Id == first.Id || team.Id == second.Id)
            .OrderBy(team => team.Name)
            .ToArrayAsync(cancellationToken);

        Assert.Equal(2, persisted.Length);
        Assert.All(persisted, team => Assert.Null(team.SpbhlTeamId));
        Assert.Equal("Local team one", persisted[0].Name);
        Assert.Equal("External snapshot", persisted[0].SpbhlTeamName);
    }

    [Fact]
    public async Task Teams_WithSameNonNullSpbhlTeamId_ViolateUniqueIndex()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var externalTeamId = Guid.NewGuid();
        await using var scope = factory.Services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var first = CreateTeam("First linked team", externalTeamId);
        var second = CreateTeam("Second linked team", externalTeamId);

        dbContext.Teams.AddRange(first, second);

        var exception = await Assert.ThrowsAsync<DbUpdateException>(() => dbContext.SaveChangesAsync(cancellationToken));
        var postgresException = Assert.IsType<PostgresException>(exception.InnerException);
        Assert.Equal(PostgresErrorCodes.UniqueViolation, postgresException.SqlState);
        Assert.Equal("i_x_teams_spbhl_team_id", postgresException.ConstraintName);
    }

    [Fact]
    public async Task ManualEvents_WithNullExternalIdentity_CanCoexist()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var scope = factory.Services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var team = CreateTeam("Manual events team");
        var first = CreateEvent(team, "Manual event one");
        var second = CreateEvent(team, "Manual event two");

        dbContext.AddRange(team, first, second);
        await dbContext.SaveChangesAsync(cancellationToken);

        Assert.Null(first.SpbhlTournamentId);
        Assert.Null(first.SpbhlMatchId);
        Assert.Null(second.SpbhlTournamentId);
        Assert.Null(second.SpbhlMatchId);
    }

    [Fact]
    public async Task Events_WithSameTeamTournamentAndMatch_ViolateUniqueIndex()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var scope = factory.Services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var team = CreateTeam("Duplicate match team");
        var first = CreateEvent(team, "Imported event one", 6537, 118101);
        var second = CreateEvent(team, "Imported event two", 6537, 118101);

        dbContext.AddRange(team, first, second);

        var exception = await Assert.ThrowsAsync<DbUpdateException>(() => dbContext.SaveChangesAsync(cancellationToken));
        var postgresException = Assert.IsType<PostgresException>(exception.InnerException);
        Assert.Equal(PostgresErrorCodes.UniqueViolation, postgresException.SqlState);
        Assert.Equal("ix_events_external_identity", postgresException.ConstraintName);
    }

    [Fact]
    public async Task SameMatchId_InDifferentTournaments_IsAllowed()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var scope = factory.Services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var team = CreateTeam("Multiple tournaments team");

        dbContext.AddRange(
            team,
            CreateEvent(team, "Tournament one", 6537, 118101),
            CreateEvent(team, "Tournament two", 6538, 118101));

        await dbContext.SaveChangesAsync(cancellationToken);
    }

    [Fact]
    public async Task SameTournamentAndMatch_ForDifferentTeams_IsAllowed()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var scope = factory.Services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var firstTeam = CreateTeam("First importing team");
        var secondTeam = CreateTeam("Second importing team");

        dbContext.AddRange(
            firstTeam,
            secondTeam,
            CreateEvent(firstTeam, "First imported event", 6537, 118101),
            CreateEvent(secondTeam, "Second imported event", 6537, 118101));

        await dbContext.SaveChangesAsync(cancellationToken);
    }

    [Fact]
    public async Task EventScores_PersistNullableAndCompletedValues()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var scope = factory.Services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var team = CreateTeam("Score persistence team");
        var scheduled = CreateEvent(team, "Scheduled imported event", 6537, 118664);
        var finished = CreateEvent(team, "Finished imported event", 6537, 118101);
        finished.HomeScore = 4;
        finished.AwayScore = 2;

        dbContext.AddRange(team, scheduled, finished);
        await dbContext.SaveChangesAsync(cancellationToken);
        dbContext.ChangeTracker.Clear();

        var persisted = await dbContext.Events.AsNoTracking()
            .Where(value => value.Id == scheduled.Id || value.Id == finished.Id)
            .ToDictionaryAsync(value => value.Id, cancellationToken);

        Assert.Null(persisted[scheduled.Id].HomeScore);
        Assert.Null(persisted[scheduled.Id].AwayScore);
        Assert.Equal(4, persisted[finished.Id].HomeScore);
        Assert.Equal(2, persisted[finished.Id].AwayScore);
    }

    [Fact]
    public async Task SpbhlSyncMetadata_RoundTripsThroughPostgreSql()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var attemptAt = new DateTime(2026, 9, 3, 8, 0, 0, DateTimeKind.Utc);
        var successfulAt = attemptAt.AddMinutes(1);
        var eventSyncedAt = attemptAt.AddMinutes(2);
        var externalTeamId = Guid.NewGuid();
        await using var scope = factory.Services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var team = CreateTeam("Local linked team", externalTeamId);
        team.SpbhlTeamName = "SPbHL snapshot name";
        team.SpbhlLastSyncAttemptAt = attemptAt;
        team.SpbhlLastSuccessfulSyncAt = successfulAt;
        var scheduledEvent = CreateEvent(team, "Imported match", 6537, 118101);
        scheduledEvent.SpbhlMatchUrl = "https://spbhl.ru/Match.aspx?TournamentID=6537&MatchID=118101";
        scheduledEvent.SpbhlLastSyncedAt = eventSyncedAt;
        scheduledEvent.ExternalMatchUrl = scheduledEvent.SpbhlMatchUrl;
        scheduledEvent.ExternalLastSyncedAt = eventSyncedAt;

        dbContext.AddRange(team, scheduledEvent);
        await dbContext.SaveChangesAsync(cancellationToken);
        dbContext.ChangeTracker.Clear();

        var persistedTeam = await dbContext.Teams.AsNoTracking()
            .SingleAsync(value => value.Id == team.Id, cancellationToken);
        var persistedEvent = await dbContext.Events.AsNoTracking()
            .SingleAsync(value => value.Id == scheduledEvent.Id, cancellationToken);

        Assert.Equal("Local linked team", persistedTeam.Name);
        Assert.Equal(externalTeamId, persistedTeam.SpbhlTeamId);
        Assert.Equal("SPbHL snapshot name", persistedTeam.SpbhlTeamName);
        Assert.Equal(attemptAt, persistedTeam.SpbhlLastSyncAttemptAt);
        Assert.Equal(successfulAt, persistedTeam.SpbhlLastSuccessfulSyncAt);
        Assert.Equal(6537, persistedEvent.SpbhlTournamentId);
        Assert.Equal(118101, persistedEvent.SpbhlMatchId);
        Assert.Equal(ExternalLeagueProvider.Spbhl, persistedEvent.ExternalLeagueProvider);
        Assert.Equal("6537", persistedEvent.ExternalCompetitionId);
        Assert.Equal("118101", persistedEvent.ExternalMatchId);
        Assert.Equal(scheduledEvent.ExternalMatchUrl, persistedEvent.ExternalMatchUrl);
        Assert.Equal(eventSyncedAt, persistedEvent.ExternalLastSyncedAt);
        Assert.Equal(scheduledEvent.SpbhlMatchUrl, persistedEvent.SpbhlMatchUrl);
        Assert.Equal(eventSyncedAt, persistedEvent.SpbhlLastSyncedAt);
    }

    private static Team CreateTeam(string name, Guid? spbhlTeamId = null)
    {
        return new Team
        {
            Name = name,
            InviteCode = Guid.NewGuid().ToString("N")[..20],
            Visibility = TeamVisibility.Private,
            CreatedByUserId = Guid.NewGuid(),
            SpbhlTeamId = spbhlTeamId
        };
    }

    private static ScheduledEvent CreateEvent(
        Team team,
        string title,
        int? tournamentId = null,
        int? matchId = null)
    {
        return new ScheduledEvent
        {
            Title = title,
            Type = EventType.Game,
            StartTime = new DateTime(2026, 7, 14, 16, 45, 0, DateTimeKind.Utc),
            Status = EventStatus.Scheduled,
            LocationName = "Test arena",
            LocationAddress = "Test address",
            Team = team,
            ExternalLeagueProvider = tournamentId.HasValue && matchId.HasValue
                ? ExternalLeagueProvider.Spbhl
                : null,
            ExternalCompetitionId = tournamentId?.ToString(),
            ExternalMatchId = matchId?.ToString(),
            SpbhlTournamentId = tournamentId,
            SpbhlMatchId = matchId
        };
    }
}
