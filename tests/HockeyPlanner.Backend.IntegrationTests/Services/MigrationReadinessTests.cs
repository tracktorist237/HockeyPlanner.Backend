using HockeyPlanner.Backend.Core.Entities;
using HockeyPlanner.Backend.Core.Enums;
using HockeyPlanner.Backend.Infrastructure.Data;
using HockeyPlanner.Backend.IntegrationTests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace HockeyPlanner.Backend.IntegrationTests.Services;

[Collection(IntegrationTestCollection.Name)]
public sealed partial class MigrationReadinessTests(HockeyPlannerWebApplicationFactory factory)
{
    private static readonly string[] RestoredIds =
    [
        "20260125121252_InitialCreate",
        "20260125125623_Attendance_RenameFieldToUser",
        "20260125133940_Line_RenameFieldToPlayers"
    ];
    private const string PreM6 = "20260906204518_AddExternalEventSuppressionsAndRemoveLateAttendance";
    private const string StagingReference = "20260928154002_AddDurableEmailAndLeagueNotificationWork";
    private static readonly DateTime Instant = new(2026, 9, 29, 10, 0, 0, DateTimeKind.Utc);

    [Fact]
    public async Task EmptyPostgresDatabase_CanApplyEntireChain_AndUseCurrentModel()
    {
        var token = TestContext.Current.CancellationToken;
        await using var database = await NewDatabaseAsync();
        await using var db = database.CreateContext();
        Assert.Empty(await db.Database.GetAppliedMigrationsAsync(token));
        await db.Database.MigrateAsync(token);
        await AssertLatestAsync(db);
        await AssertModelColumnsAsync(db);
        await CurrentModelSmokeAsync(db);
    }

    [Theory]
    [InlineData("20260125133940_Line_RenameFieldToPlayers")]
    [InlineData(PreM6)]
    public async Task ExistingInstallation_SkipsRestoredIds_PreservesData_AndAppliesOnlyPending(string baseline)
    {
        var token = TestContext.Current.CancellationToken;
        await using var database = await NewDatabaseAsync();
        await using (var setup = database.CreateContext())
            await setup.GetService<IMigrator>().MigrateAsync(baseline, token);

        var userId = Guid.NewGuid();
        var eventId = Guid.NewGuid();
        var lineId = Guid.NewGuid();
        var playerId = Guid.NewGuid();
        var attendanceId = Guid.NewGuid();
        var notificationId = Guid.NewGuid();
        string[] previouslyApplied;
        string[] pending;
        await using (var seed = database.CreateContext())
        {
            previouslyApplied = (await seed.Database.GetAppliedMigrationsAsync(token)).ToArray();
            Assert.Equal(RestoredIds, previouslyApplied.Take(3));
            pending = (await seed.Database.GetPendingMigrationsAsync(token)).ToArray();
            Assert.DoesNotContain(pending, value => RestoredIds.Contains(value));
            await seed.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO users (id, first_name, last_name, role, created_at)
                VALUES ({userId}, 'Historical', 'Player', 1, {Instant});
                """, token);
            if (baseline == RestoredIds[2])
                await seed.Database.ExecuteSqlInterpolatedAsync($"""
                    INSERT INTO events (id, title, type, start_time, end_time, status, location_name, location_address, created_at)
                    VALUES ({eventId}, 'Historical event', 1, {Instant}, {Instant.AddHours(1)}, 1, 'Arena', 'Address', {Instant});
                    """, token);
            else
                await seed.Database.ExecuteSqlInterpolatedAsync($"""
                    INSERT INTO events (id, title, type, start_time, duration_minutes, status, location_name, location_address, created_at)
                    VALUES ({eventId}, 'Historical event', 1, {Instant}, 60, 1, 'Arena', 'Address', {Instant});
                    """, token);
            await seed.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO lines (id, name, "order", event_id, created_at)
                VALUES ({lineId}, 'Historical line', 1, {eventId}, {Instant});
                INSERT INTO players (id, line_id, user_id, role, first_name, last_name, created_at)
                VALUES ({playerId}, {lineId}, {userId}, 1, 'Historical', 'Player', {Instant});
                INSERT INTO attendances (id, event_id, user_id, status, responded_at, notes, created_at)
                VALUES ({attendanceId}, {eventId}, {userId}, 2, {Instant}, 'Keep this answer', {Instant});
                """, token);
            if (baseline == PreM6)
                await seed.Database.ExecuteSqlInterpolatedAsync($"""
                    INSERT INTO notifications (id, user_id, type, category, title, body, is_read, created_at)
                    VALUES ({notificationId}, {userId}, 1, 1, 'Historical notification', 'Keep this notification', false, {Instant});
                    """, token);
        }

        var applying = new List<string>();
        await using var upgraded = database.CreateContext(applying);
        await upgraded.Database.MigrateAsync(token);
        Assert.Equal(pending.Length, applying.Count);
        foreach (var id in pending) Assert.Contains(applying, message => message.Contains(id, StringComparison.Ordinal));
        Assert.DoesNotContain(applying, message => RestoredIds.Any(id => message.Contains(id, StringComparison.Ordinal)));
        await AssertLatestAsync(upgraded);
        Assert.Equal(previouslyApplied, (await upgraded.Database.GetAppliedMigrationsAsync(token)).Take(previouslyApplied.Length));
        Assert.Equal("Historical", (await upgraded.Users.SingleAsync(value => value.Id == userId, token)).FirstName);
        Assert.Equal("Historical event", (await upgraded.Events.SingleAsync(value => value.Id == eventId, token)).Title);
        Assert.Equal("Historical line", (await upgraded.Lines.SingleAsync(value => value.Id == lineId, token)).Name);
        Assert.Equal(userId, (await upgraded.Players.SingleAsync(value => value.Id == playerId, token)).UserId);
        var attendance = await upgraded.Attendances.SingleAsync(value => value.Id == attendanceId, token);
        Assert.Equal(AttendanceStatus.Confirmed, attendance.Status);
        Assert.Equal("Keep this answer", attendance.Notes);
        if (baseline == PreM6)
        {
            Assert.Equal(new[] { "20260928151908_AddNotificationJobs",
                "20260928152829_AddNotificationLogicalIdentity",
                "20260928154002_AddDurableEmailAndLeagueNotificationWork" }, pending.Take(3));
            var notification = await upgraded.Notifications.SingleAsync(value => value.Id == notificationId, token);
            Assert.Equal("Keep this notification", notification.Body);
            Assert.Null(notification.LogicalKey);
            Assert.Empty(await upgraded.NotificationJobs.ToListAsync(token));
        }
    }

    [Fact]
    public async Task StagingHistoryReference_RepeatedMigrateIsNoOp_AndPreservesDurableWork()
    {
        var token = TestContext.Current.CancellationToken;
        await using var database = await NewDatabaseAsync();
        await using (var setup = database.CreateContext())
            await setup.GetService<IMigrator>().MigrateAsync(StagingReference, token);
        var applying = new List<string>();
        await using var db = database.CreateContext(applying);
        var history = (await db.Database.GetAppliedMigrationsAsync(token)).ToArray();
        // Frozen reference from the post-M6 installation, including the three historical IDs.
        var reference = await File.ReadAllLinesAsync(Path.Combine(AppContext.BaseDirectory,
            "Fixtures", "Migrations", "staging-post-m6-history.txt"), token);
        Assert.Equal(reference, history);
        var jobId = await CurrentModelSmokeAsync(db);
        await db.Database.MigrateAsync(token);
        var latestHistory = (await db.Database.GetAppliedMigrationsAsync(token)).ToArray();
        Assert.Equal(history, latestHistory.Take(history.Length));
        Assert.Equal(db.Database.GetMigrations().Skip(history.Length).Count(), applying.Count);
        applying.Clear();
        await db.Database.MigrateAsync(token);
        Assert.Empty(applying);
        Assert.Equal(latestHistory, await db.Database.GetAppliedMigrationsAsync(token));
        db.ChangeTracker.Clear();
        var job = await db.NotificationJobs.Include(value => value.Notification).SingleAsync(value => value.Id == jobId, token);
        Assert.Equal(NotificationJobStatus.Pending, job.Status);
        Assert.Equal("Migration smoke", job.Notification!.Title);
        await AssertLatestAsync(db);
    }

    [Fact]
    public async Task FullAndIdempotentScripts_ApplyOnEmptyDatabase_AndReplayWithoutRepeatingHistoricalOperations()
    {
        var token = TestContext.Current.CancellationToken;
        await using var database = await NewDatabaseAsync();
        await using var db = database.CreateContext();
        var migrator = db.GetService<IMigrator>();
        var freshScript = migrator.GenerateScript();
        var idempotent = migrator.GenerateScript(options: MigrationsSqlGenerationOptions.Idempotent);
        foreach (var id in RestoredIds)
            Assert.Contains($"IF NOT EXISTS(SELECT 1 FROM \"__EFMigrationsHistory\" WHERE \"MigrationId\" = '{id}')", idempotent);
        await using var connection = new NpgsqlConnection(database.ConnectionString);
        await connection.OpenAsync(token);
        await using (var fresh = new NpgsqlCommand(freshScript, connection))
            await fresh.ExecuteNonQueryAsync(token);
        var jobId = await CurrentModelSmokeAsync(db);
        await using (var replay = new NpgsqlCommand(idempotent, connection))
            await replay.ExecuteNonQueryAsync(token);
        await AssertLatestAsync(db);
        Assert.True(await db.NotificationJobs.AnyAsync(value => value.Id == jobId, token));

        await using var second = await NewDatabaseAsync();
        await using var secondConnection = new NpgsqlConnection(second.ConnectionString);
        await secondConnection.OpenAsync(token);
        await using (var firstIdempotentRun = new NpgsqlCommand(idempotent, secondConnection))
            await firstIdempotentRun.ExecuteNonQueryAsync(token);
        await using var secondDb = second.CreateContext();
        await AssertLatestAsync(secondDb);
    }

    private static async Task AssertLatestAsync(AppDbContext db)
    {
        var token = TestContext.Current.CancellationToken;
        var expected = db.Database.GetMigrations().ToArray();
        Assert.Equal(RestoredIds, expected.Take(3));
        Assert.Equal(expected, await db.Database.GetAppliedMigrationsAsync(token));
        Assert.Empty(await db.Database.GetPendingMigrationsAsync(token));
        Assert.False(db.Database.HasPendingModelChanges());
    }

    private static async Task<Guid> CurrentModelSmokeAsync(AppDbContext db)
    {
        var token = TestContext.Current.CancellationToken;
        var user = new User { FirstName = "Migration", LastName = "Smoke" };
        var scheduledEvent = new ScheduledEvent { Title = "Migration event", StartTime = Instant,
            DurationMinutes = 60, LocationName = "Arena", LocationAddress = "Address" };
        var notification = new Notification { UserId = user.Id, Title = "Migration smoke",
            Body = "Test only", Type = NotificationType.EventPublished,
            Category = NotificationCategory.AttendanceRequired, Url = $"/events/{scheduledEvent.Id}" };
        var job = new NotificationJob { NotificationId = notification.Id, NextAttemptAt = Instant };
        db.AddRange(user, scheduledEvent, notification, job,
            new Attendance { EventId = scheduledEvent.Id, UserId = user.Id, Status = AttendanceStatus.Confirmed });
        await db.SaveChangesAsync(token);
        db.ChangeTracker.Clear();
        Assert.Equal(60, (await db.Events.SingleAsync(value => value.Id == scheduledEvent.Id, token)).DurationMinutes);
        Assert.Equal(AttendanceStatus.Confirmed, (await db.Attendances.SingleAsync(
            value => value.EventId == scheduledEvent.Id, token)).Status);
        Assert.Equal(user.Id, (await db.NotificationJobs.Include(value => value.Notification)
            .SingleAsync(value => value.Id == job.Id, token)).Notification!.UserId);
        return job.Id;
    }

    private static async Task AssertModelColumnsAsync(AppDbContext db)
    {
        // Inspect the resulting PostgreSQL schema, not just EF's snapshot/migration history.
        var expected = db.Model.GetRelationalModel().Tables.SelectMany(table => table.Columns.Select(column =>
            $"{table.Name}|{column.Name}|{column.StoreType}|{column.IsNullable}")).Order().ToArray();
        await using var connection = new NpgsqlConnection(db.Database.GetConnectionString());
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = new NpgsqlCommand("""
            SELECT c.relname, a.attname, format_type(a.atttypid, a.atttypmod), NOT a.attnotnull
            FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace
            JOIN pg_attribute a ON a.attrelid = c.oid
            WHERE n.nspname = 'public' AND c.relkind = 'r' AND a.attnum > 0 AND NOT a.attisdropped
              AND c.relname <> '__EFMigrationsHistory'
            """, connection);
        await using var reader = await command.ExecuteReaderAsync(TestContext.Current.CancellationToken);
        var actual = new List<string>();
        while (await reader.ReadAsync(TestContext.Current.CancellationToken))
            actual.Add($"{reader.GetString(0)}|{reader.GetString(1)}|{reader.GetString(2)}|{reader.GetBoolean(3)}");
        Assert.Equal(expected, actual.Order().ToArray());
    }

    private async Task<IsolatedMigrationDatabase> NewDatabaseAsync()
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var original = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var connection = new NpgsqlConnectionStringBuilder(original.Database.GetConnectionString());
        Assert.StartsWith("hockeyplanner_test_", connection.Database);
        Assert.Equal(factory.MappedPostgreSqlPort, connection.Port);
        var name = $"hp_migration_test_{Guid.NewGuid():N}";
        await using var admin = new NpgsqlConnection(connection.ConnectionString);
        await admin.OpenAsync(TestContext.Current.CancellationToken);
        await using var create = new NpgsqlCommand($"CREATE DATABASE {new NpgsqlCommandBuilder().QuoteIdentifier(name)}", admin);
        await create.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        var adminConnection = connection.ConnectionString;
        connection.Database = name;
        connection.Pooling = false;
        return new IsolatedMigrationDatabase(adminConnection, connection.ConnectionString, name);
    }

    private sealed class IsolatedMigrationDatabase(string adminConnection, string connectionString, string name) : IAsyncDisposable
    {
        public string ConnectionString => connectionString;

        public AppDbContext CreateContext(List<string>? applying = null)
        {
            var options = new DbContextOptionsBuilder<AppDbContext>().UseNpgsql(connectionString);
            if (applying is not null) options.LogTo(applying.Add, [RelationalEventId.MigrationApplying]);
            return new AppDbContext(options.Options);
        }

        public async ValueTask DisposeAsync()
        {
            // Only the random database created above on the validated Testcontainer connection.
            await using var admin = new NpgsqlConnection(adminConnection);
            await admin.OpenAsync();
            await using var drop = new NpgsqlCommand(
                $"DROP DATABASE {new NpgsqlCommandBuilder().QuoteIdentifier(name)} WITH (FORCE)", admin);
            await drop.ExecuteNonQueryAsync();
        }
    }
}
