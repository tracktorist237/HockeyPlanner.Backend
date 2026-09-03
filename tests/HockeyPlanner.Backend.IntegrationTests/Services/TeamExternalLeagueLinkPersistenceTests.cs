using HockeyPlanner.Backend.Core.Entities;
using HockeyPlanner.Backend.Core.Enums;
using HockeyPlanner.Backend.Infrastructure.Data;
using HockeyPlanner.Backend.IntegrationTests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace HockeyPlanner.Backend.IntegrationTests.Services;

[Collection(IntegrationTestCollection.Name)]
[Trait("Category", "ExternalLeagueLinkPersistence")]
public sealed class TeamExternalLeagueLinkPersistenceTests(HockeyPlannerWebApplicationFactory factory)
{
    private const string CurrentMigration = "20260903195604_AddExternalLeagueTeamLinks";

    [Fact]
    public async Task Migration_BackfillsOnlyLegacyLinkedTeams()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var scope = factory.Services.CreateAsyncScope();
        var baseContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var schema = $"external_link_migration_{Guid.NewGuid():N}";
        var quotedSchema = new NpgsqlCommandBuilder().QuoteIdentifier(schema);
        await ExecuteAdminCommandAsync(
            baseContext.Database.GetConnectionString()!,
            $"CREATE SCHEMA {quotedSchema}",
            cancellationToken);

        try
        {
            var connectionString = new NpgsqlConnectionStringBuilder(baseContext.Database.GetConnectionString())
            {
                SearchPath = schema
            }.ConnectionString;
            var options = new DbContextOptionsBuilder<AppDbContext>()
                .UseNpgsql(connectionString, builder => builder.MigrationsAssembly(typeof(AppDbContext).Assembly.FullName))
                .Options;

            await using var migrationContext = new AppDbContext(options);
            var migrator = migrationContext.GetService<IMigrator>();
            var attemptAt = new DateTime(2026, 9, 3, 10, 0, 0, DateTimeKind.Utc);
            var successfulAt = attemptAt.AddMinutes(1);
            var spbhlTeamId = Guid.NewGuid();
            var linkedTeamId = Guid.NewGuid();
            var unlinkedTeamId = Guid.NewGuid();

            await migrationContext.Database.ExecuteSqlRawAsync(
                """
                CREATE TABLE "__EFMigrationsHistory" (
                    "MigrationId" character varying(150) NOT NULL,
                    "ProductVersion" character varying(32) NOT NULL,
                    CONSTRAINT "PK___EFMigrationsHistory" PRIMARY KEY ("MigrationId")
                );
                CREATE TABLE teams (
                    id uuid NOT NULL PRIMARY KEY,
                    spbhl_team_id uuid NULL,
                    spbhl_team_name character varying(200) NULL,
                    spbhl_last_sync_attempt_at timestamp with time zone NULL,
                    spbhl_last_successful_sync_at timestamp with time zone NULL,
                    created_at timestamp with time zone NOT NULL
                );
                """,
                cancellationToken);

            foreach (var migration in migrationContext.Database.GetMigrations().Where(value => value != CurrentMigration))
            {
                await migrationContext.Database.ExecuteSqlInterpolatedAsync(
                    $"INSERT INTO \"__EFMigrationsHistory\" (\"MigrationId\", \"ProductVersion\") VALUES ({migration}, {"10.0.2"})",
                    cancellationToken);
            }

            await migrationContext.Database.ExecuteSqlInterpolatedAsync(
                $"""
                INSERT INTO teams
                    (id, spbhl_team_id, spbhl_team_name, spbhl_last_sync_attempt_at,
                     spbhl_last_successful_sync_at, created_at)
                VALUES
                    ({linkedTeamId}, {spbhlTeamId}, {"Северная столица"}, {attemptAt}, {successfulAt}, {attemptAt}),
                    ({unlinkedTeamId}, NULL, NULL, NULL, NULL, {attemptAt});
                """,
                cancellationToken);

            await migrator.MigrateAsync(null, cancellationToken);

            var link = await migrationContext.TeamExternalLeagueLinks.AsNoTracking().SingleAsync(cancellationToken);
            Assert.Equal(linkedTeamId, link.TeamId);
            Assert.Equal(ExternalLeagueProvider.Spbhl, link.Provider);
            Assert.Equal(spbhlTeamId.ToString(), link.ExternalTeamId);
            Assert.Equal("Северная столица", link.ExternalTeamName);
            Assert.True(link.IsPrimary);
            Assert.Equal(attemptAt, link.LastSyncAttemptAt);
            Assert.Equal(successfulAt, link.LastSuccessfulSyncAt);
            Assert.False(await migrationContext.TeamExternalLeagueLinks.AsNoTracking()
                .AnyAsync(value => value.TeamId == unlinkedTeamId, cancellationToken));
        }
        finally
        {
            await ExecuteAdminCommandAsync(
                baseContext.Database.GetConnectionString()!,
                $"DROP SCHEMA IF EXISTS {quotedSchema} CASCADE",
                cancellationToken);
        }
    }

    [Fact]
    public async Task Team_CanHaveMultipleLinksForSameProvider()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var scope = factory.Services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var team = CreateTeam("Northern capital");
        team.ExternalLeagueLinks.Add(CreateLink(ExternalLeagueProvider.Spbhl, Guid.NewGuid().ToString(), "Северная столица", true));
        team.ExternalLeagueLinks.Add(CreateLink(ExternalLeagueProvider.Spbhl, Guid.NewGuid().ToString(), "Северная столица-2", false));
        context.Teams.Add(team);

        await context.SaveChangesAsync(cancellationToken);

        Assert.Equal(2, await context.TeamExternalLeagueLinks.CountAsync(value => value.TeamId == team.Id, cancellationToken));
    }

    [Fact]
    public async Task SameProviderAndExternalTeamId_CannotBelongToDifferentTeams()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var scope = factory.Services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var externalTeamId = Guid.NewGuid().ToString();
        var firstTeam = CreateTeam("First local team");
        var secondTeam = CreateTeam("Second local team");
        firstTeam.ExternalLeagueLinks.Add(CreateLink(ExternalLeagueProvider.Spbhl, externalTeamId, "External team", true));
        secondTeam.ExternalLeagueLinks.Add(CreateLink(ExternalLeagueProvider.Spbhl, externalTeamId, "External team", true));
        context.Teams.AddRange(firstTeam, secondTeam);

        var exception = await Assert.ThrowsAsync<DbUpdateException>(() => context.SaveChangesAsync(cancellationToken));
        var postgresException = Assert.IsType<PostgresException>(exception.InnerException);
        Assert.Equal(PostgresErrorCodes.UniqueViolation, postgresException.SqlState);
        Assert.Equal("i_x_team_external_league_links_provider_external_team_id", postgresException.ConstraintName);
    }

    [Fact]
    public async Task SameExternalTeamId_ForDifferentProvider_IsAllowed()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var scope = factory.Services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var externalTeamId = Guid.NewGuid().ToString();
        var firstTeam = CreateTeam("SPbHL local team");
        var secondTeam = CreateTeam("Future provider local team");
        firstTeam.ExternalLeagueLinks.Add(CreateLink(ExternalLeagueProvider.Spbhl, externalTeamId, "SPbHL team", true));
        secondTeam.ExternalLeagueLinks.Add(CreateLink((ExternalLeagueProvider)2, externalTeamId, "Future provider team", true));
        context.Teams.AddRange(firstTeam, secondTeam);

        await context.SaveChangesAsync(cancellationToken);

        Assert.Equal(2, await context.TeamExternalLeagueLinks.CountAsync(
            value => value.ExternalTeamId == externalTeamId,
            cancellationToken));
    }

    private static Team CreateTeam(string name) => new()
    {
        Name = name,
        InviteCode = Guid.NewGuid().ToString("N")[..20],
        Visibility = TeamVisibility.Private,
        CreatedByUserId = Guid.NewGuid()
    };

    private static TeamExternalLeagueLink CreateLink(
        ExternalLeagueProvider provider,
        string externalTeamId,
        string externalTeamName,
        bool isPrimary) => new()
    {
        Provider = provider,
        ExternalTeamId = externalTeamId,
        ExternalTeamName = externalTeamName,
        IsPrimary = isPrimary
    };

    private static async Task ExecuteAdminCommandAsync(
        string connectionString,
        string commandText,
        CancellationToken cancellationToken)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new NpgsqlCommand(commandText, connection);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
