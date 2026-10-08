using HockeyPlanner.Backend.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;

namespace HockeyPlanner.Backend.IntegrationTests.Services;

public sealed partial class MigrationReadinessTests
{
    [Fact]
    [Trait("Category", "HP84")]
    public async Task Hp84_ExistingPostM6_BackfillPreservesEveryLegacyField_AndRollbackPreservesRows()
    {
        var token = TestContext.Current.CancellationToken;
        await using var database = await NewDatabaseAsync();
        await using var db = database.CreateContext();
        await db.GetService<IMigrator>().MigrateAsync(StagingReference, token);
        var seed = await SeedHp84Legacy(db);
        var before = await Hp84Rows(db);
        var cleanReport = await Hp84Preflight(database.ConnectionString);
        Assert.Equal((0L, "PASS"), cleanReport);
        Assert.Equal(before, await Hp84Rows(db));
        await db.Database.MigrateAsync(token);
        await AssertLatestAsync(db);
        await AssertModelColumnsAsync(db);
        Assert.Equal(before, await Hp84Rows(db));
        var players = await db.Players.AsNoTracking().ToListAsync(token);
        Assert.Equal(2, players.Count);
        Assert.All(players, p => Assert.Equal(seed.Event, p.EventId));

        // Inspect actual PostgreSQL constraints/index definitions after real migration.
        await using var connection = new NpgsqlConnection(database.ConnectionString);
        await connection.OpenAsync(token);
        await using var constraints = new NpgsqlCommand("""
            SELECT string_agg(pg_get_constraintdef(oid), E'\n' ORDER BY conname)
            FROM pg_constraint WHERE conrelid = 'players'::regclass AND contype = 'f';
            """, connection);
        var definitions = (string)(await constraints.ExecuteScalarAsync(token))!;
        Assert.Contains("FOREIGN KEY (line_id, event_id) REFERENCES lines(id, event_id) ON DELETE CASCADE", definitions);
        Assert.Contains("FOREIGN KEY (event_guest_id, event_id) REFERENCES event_guests(id, event_id) ON DELETE RESTRICT", definitions);
        await using var indexes = new NpgsqlCommand("SELECT string_agg(indexdef, E'\n') FROM pg_indexes WHERE tablename = 'players'", connection);
        var indexDefinitions = (string)(await indexes.ExecuteScalarAsync(token))!;
        Assert.Contains("UNIQUE INDEX ux_players_event_user", indexDefinitions);
        Assert.Contains("(event_id, user_id) WHERE (user_id IS NOT NULL)", indexDefinitions);
        Assert.Contains("UNIQUE INDEX ux_players_event_guest", indexDefinitions);

        await db.Database.MigrateAsync(token);
        Assert.Equal(before, await Hp84Rows(db));
        await db.GetService<IMigrator>().MigrateAsync(StagingReference, token);
        Assert.Equal(before, await Hp84Rows(db));
        // Up -> Down -> Up rehearses adoption without losing user/guest roster/attendance.
        await db.Database.MigrateAsync(token);
        Assert.Equal(before, await Hp84Rows(db));
        await AssertLatestAsync(db);
    }

    [Theory]
    [InlineData("duplicate_user")]
    [InlineData("duplicate_guest")]
    [InlineData("foreign_guest")]
    [InlineData("missing_event")]
    [Trait("Category", "HP84")]
    public async Task Hp84_LegacyAnomalies_FailClosedWithoutSchemaHistoryOrDataMutation(string anomaly)
    {
        var token = TestContext.Current.CancellationToken;
        await using var database = await NewDatabaseAsync();
        await using var db = database.CreateContext();
        await db.GetService<IMigrator>().MigrateAsync(StagingReference, token);
        var seed = await SeedHp84Legacy(db);
        if (anomaly is "duplicate_user" or "duplicate_guest")
        {
            var player = Guid.NewGuid();
            var source = anomaly == "duplicate_user" ? seed.UserPlayer : seed.GuestPlayer;
            var line = anomaly == "duplicate_user" ? seed.SecondLine : seed.FirstLine;
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO players (id, line_id, user_id, event_guest_id, role, first_name, last_name, created_at)
                SELECT {player}, {line}, user_id, event_guest_id, role, first_name, last_name, created_at
                FROM players WHERE id = {source};
                """, token);
        }
        else if (anomaly == "foreign_guest")
            await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE event_guests SET event_id = {seed.OtherEvent} WHERE id = {seed.Guest}", token);
        else
            // Controlled legacy corruption in an isolated superuser-owned test DB.
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                ALTER TABLE lines DISABLE TRIGGER ALL;
                UPDATE lines SET event_id = {Guid.NewGuid()} WHERE id = {seed.FirstLine};
                ALTER TABLE lines ENABLE TRIGGER ALL;
                """, token);
        var before = await Hp84Rows(db);
        var history = (await db.Database.GetAppliedMigrationsAsync(token)).ToArray();
        var report = await Hp84Preflight(database.ConnectionString);
        Assert.True(report.Count > 0, "Operator preflight must report the anomaly before migration.");
        Assert.Equal("BLOCKED", report.Status);
        Assert.Equal(before, await Hp84Rows(db));
        Assert.Equal(history, await db.Database.GetAppliedMigrationsAsync(token));
        var error = await Assert.ThrowsAsync<PostgresException>(() => db.Database.MigrateAsync(token));
        Assert.Equal(PostgresErrorCodes.RaiseException, error.SqlState);
        Assert.Equal("HP84 roster preflight failed; operator reconciliation is required. No data was removed.", error.MessageText);
        Assert.Equal(before, await Hp84Rows(db));
        Assert.Equal(history, await db.Database.GetAppliedMigrationsAsync(token));
        await using var check = new NpgsqlConnection(database.ConnectionString);
        await check.OpenAsync(token);
        await using var column = new NpgsqlCommand("SELECT COUNT(*) FROM information_schema.columns WHERE table_name = 'players' AND column_name = 'event_id'", check);
        Assert.Equal(0L, await column.ExecuteScalarAsync(token));
        // Repeated failed migrate remains non-destructive and does not fabricate history.
        await Assert.ThrowsAsync<PostgresException>(() => db.Database.MigrateAsync(token));
        Assert.Equal(before, await Hp84Rows(db));
        Assert.Equal(history, await db.Database.GetAppliedMigrationsAsync(token));
    }

    [Fact]
    [Trait("Category", "HP84")]
    public async Task Hp84_MigrationWaitsForLegacyWriter_ThenRechecksItsCommittedAnomalyUnderTableLocks()
    {
        var token = TestContext.Current.CancellationToken;
        await using var database = await NewDatabaseAsync();
        await using var db = database.CreateContext();
        await db.GetService<IMigrator>().MigrateAsync(StagingReference, token);
        var seed = await SeedHp84Legacy(db);
        await using var writer = new NpgsqlConnection(database.ConnectionString);
        await writer.OpenAsync(token);
        await using var transaction = await writer.BeginTransactionAsync(token);
        await using (var insert = new NpgsqlCommand("""
            INSERT INTO players (id, line_id, user_id, role, first_name, last_name, created_at)
            SELECT @player, @line, user_id, role, first_name, last_name, created_at
            FROM players WHERE id = @source;
            """, writer, transaction))
        {
            insert.Parameters.AddWithValue("player", Guid.NewGuid());
            insert.Parameters.AddWithValue("line", seed.SecondLine);
            insert.Parameters.AddWithValue("source", seed.UserPlayer);
            Assert.Equal(1, await insert.ExecuteNonQueryAsync(token));
        }
        await db.Database.OpenConnectionAsync(token);
        var migrationPid = ((NpgsqlConnection)db.Database.GetDbConnection()).ProcessID;
        var migration = db.Database.MigrateAsync(token);
        try
        {
            await using var monitor = new NpgsqlConnection(database.ConnectionString);
            await monitor.OpenAsync(token);
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
            deadline.CancelAfter(TimeSpan.FromSeconds(20));
            while (true)
            {
                Assert.False(migration.IsCompleted, "The migration must wait for the active legacy writer.");
                await using var blocking = new NpgsqlCommand("SELECT pg_blocking_pids(@pid)", monitor);
                blocking.Parameters.AddWithValue("pid", migrationPid);
                if (((int[])(await blocking.ExecuteScalarAsync(deadline.Token))!).Contains(writer.ProcessID)) break;
            }
        }
        finally
        {
            await transaction.CommitAsync(token);
        }
        var error = await Assert.ThrowsAsync<PostgresException>(() => migration);
        Assert.Equal(PostgresErrorCodes.RaiseException, error.SqlState);
        Assert.StartsWith("HP84 roster preflight failed", error.MessageText);
        await using var verify = database.CreateContext();
        await using var check = new NpgsqlConnection(database.ConnectionString);
        await check.OpenAsync(token);
        await using var count = new NpgsqlCommand("SELECT COUNT(*) FROM players", check);
        Assert.Equal(3L, await count.ExecuteScalarAsync(token));
        Assert.Equal(StagingReference, (await verify.Database.GetAppliedMigrationsAsync(token)).Last());
    }

    private static async Task<Hp84Legacy> SeedHp84Legacy(AppDbContext db)
    {
        var token = TestContext.Current.CancellationToken;
        var seed = new Hp84Legacy(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO users (id, first_name, last_name, role, created_at)
            VALUES ({seed.User}, 'Legacy', 'User', 1, {Instant});
            INSERT INTO events (id, title, type, start_time, duration_minutes, status, location_name, location_address, created_at)
            VALUES ({seed.Event}, 'Legacy roster', 1, {Instant}, 60, 1, 'Arena', 'Address', {Instant}),
                   ({seed.OtherEvent}, 'Other event', 1, {Instant.AddDays(1)}, 60, 1, 'Arena', 'Address', {Instant});
            INSERT INTO lines (id, event_id, name, "order", created_at)
            VALUES ({seed.FirstLine}, {seed.Event}, 'First', 1, {Instant}),
                   ({seed.SecondLine}, {seed.Event}, 'Second', 2, {Instant});
            INSERT INTO event_guests (id, event_id, invited_by_user_id, first_name, last_name, status, responded_at, created_at)
            VALUES ({seed.Guest}, {seed.Event}, {seed.User}, 'Legacy', 'Guest', 2, {Instant}, {Instant});
            INSERT INTO players (id, line_id, user_id, event_guest_id, first_name, last_name, role, jersey_number, handedness, created_at, updated_at)
            VALUES ({seed.UserPlayer}, {seed.FirstLine}, {seed.User}, NULL, 'Legacy', 'User', 1, 17, 1, {Instant}, {Instant}),
                   ({seed.GuestPlayer}, {seed.SecondLine}, NULL, {seed.Guest}, 'Legacy', 'Guest', 2, 23, NULL, {Instant}, NULL);
            INSERT INTO attendances (id, event_id, user_id, status, notes, responded_at, created_at, updated_at)
            VALUES ({Guid.NewGuid()}, {seed.Event}, {seed.User}, 2, 'Preserve answer', {Instant}, {Instant}, {Instant});
            """, token);
        return seed;
    }

    private static async Task<(long Count, string Status)> Hp84Preflight(string connectionString)
    {
        var token = TestContext.Current.CancellationToken;
        var sql = await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory,
            "Fixtures", "Migrations", "hp84-roster-preflight.sql"), token);
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(token);
        await using var command = new NpgsqlCommand(sql, connection);
        await using var reader = await command.ExecuteReaderAsync(token);
        while (reader.FieldCount == 0 && await reader.NextResultAsync(token)) { }
        Assert.True(await reader.ReadAsync(token));
        Assert.Equal(1, reader.GetInt32(reader.GetOrdinal("schema_version")));
        var count = reader.GetInt64(reader.GetOrdinal("anomaly_count"));
        var status = reader.GetString(reader.GetOrdinal("status"));
        Assert.DoesNotContain("user_id", Enumerable.Range(0, reader.FieldCount).Select(reader.GetName));
        Assert.DoesNotContain("event_id", Enumerable.Range(0, reader.FieldCount).Select(reader.GetName));
        Assert.False(await reader.ReadAsync(token));
        while (await reader.NextResultAsync(token)) { } // Includes the final ROLLBACK.
        return (count, status);
    }

    private static async Task<string> Hp84Rows(AppDbContext db)
    {
        await using var connection = new NpgsqlConnection(db.Database.GetConnectionString());
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = new NpgsqlCommand("""
            SELECT jsonb_build_object(
                'players', (SELECT jsonb_agg(to_jsonb(p) - 'event_id' ORDER BY p.id) FROM players p),
                'lines', (SELECT jsonb_agg(to_jsonb(l) ORDER BY l.id) FROM lines l),
                'guests', (SELECT jsonb_agg(to_jsonb(g) ORDER BY g.id) FROM event_guests g),
                'attendance', (SELECT jsonb_agg(to_jsonb(a) ORDER BY a.id) FROM attendances a))::text;
            """, connection);
        return (string)(await command.ExecuteScalarAsync(TestContext.Current.CancellationToken))!;
    }

    private sealed record Hp84Legacy(Guid User, Guid Event, Guid OtherEvent, Guid FirstLine, Guid SecondLine,
        Guid Guest, Guid UserPlayer, Guid GuestPlayer);
}
