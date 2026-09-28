using HockeyPlanner.Backend.Core.Entities;
using HockeyPlanner.Backend.Infrastructure.Data;
using HockeyPlanner.Backend.IntegrationTests.Fixtures;
using HockeyPlanner.Backend.IntegrationTests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace HockeyPlanner.Backend.IntegrationTests.Services;

[Collection(IntegrationTestCollection.Name)]
public sealed class NotificationJobPersistenceTests(HockeyPlannerWebApplicationFactory factory)
{
    [Fact]
    public async Task Migration_CreatesQueueWithoutChangingExistingNotifications()
    {
        var token = TestContext.Current.CancellationToken;
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var schema = $"notification_migration_{Guid.NewGuid():N}";
        await using var connection = new NpgsqlConnection(db.Database.GetConnectionString());
        await connection.OpenAsync(token);
        await using var transaction = await connection.BeginTransactionAsync(token);
        await using var setup = new NpgsqlCommand($"CREATE SCHEMA {schema}; SET LOCAL search_path TO {schema}; CREATE TABLE notifications (id uuid PRIMARY KEY);", connection, transaction);
        await setup.ExecuteNonQueryAsync(token);
        var assembly = db.GetService<IMigrationsAssembly>();
        var migration = assembly.CreateMigration(assembly.Migrations.Single(value => value.Key.EndsWith("_AddNotificationJobs")).Value, db.Database.ProviderName!);
        foreach (var command in db.GetService<IMigrationsSqlGenerator>().Generate(migration.UpOperations))
        {
            await using var apply = new NpgsqlCommand(command.CommandText, connection, transaction);
            await apply.ExecuteNonQueryAsync(token);
        }
        await using var verify = new NpgsqlCommand("SELECT count(*) FROM pg_indexes WHERE schemaname = @schema AND tablename = 'notification_jobs'", connection, transaction);
        verify.Parameters.AddWithValue("schema", schema);
        Assert.Equal(4L, await verify.ExecuteScalarAsync(token));
        Assert.DoesNotContain(migration.UpOperations, operation => operation is Microsoft.EntityFrameworkCore.Migrations.Operations.DropTableOperation);
        await transaction.RollbackAsync(token);
    }

    [Fact]
    public async Task CommittedJob_SurvivesNewContext_WithoutCopyingMessageOrCredentials()
    {
        var token = TestContext.Current.CancellationToken;
        var scenario = await TwoUserNotificationScenarioBuilder.CreateAsync(factory.Services, token);
        Guid jobId;
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var job = new NotificationJob { NotificationId = scenario.UserAUnread.Id, NextAttemptAt = DateTime.UtcNow };
            context.NotificationJobs.Add(job);
            await context.SaveChangesAsync(token);
            jobId = job.Id;
        }
        await using var verification = factory.Services.CreateAsyncScope();
        var db = verification.ServiceProvider.GetRequiredService<AppDbContext>();
        var stored = await db.NotificationJobs.SingleAsync(value => value.Id == jobId, token);
        Assert.Equal(scenario.UserAUnread.Id, stored.NotificationId);
        Assert.Equal(0, stored.AttemptCount);
        Assert.Null(stored.ClaimId);
        Assert.Null(stored.ProtectedPayload);
        Assert.Null(stored.TokenRecordId);
        Assert.DoesNotContain(db.Model.FindEntityType(typeof(NotificationJob))!.GetProperties(),
            property => property.Name.Contains("Endpoint") || property.Name == "Body" || property.Name == "RawToken");
    }

    [Fact]
    public async Task BusinessChangeAndJob_RollBackTogether()
    {
        var token = TestContext.Current.CancellationToken;
        var scenario = await TwoUserNotificationScenarioBuilder.CreateAsync(factory.Services, token);
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            await using var transaction = await db.Database.BeginTransactionAsync(token);
            var user = await db.Users.SingleAsync(value => value.Id == scenario.UserA.Id, token);
            user.FirstName = "Rolled back";
            db.NotificationJobs.Add(new NotificationJob { NotificationId = scenario.UserAUnread.Id, NextAttemptAt = DateTime.UtcNow });
            await db.SaveChangesAsync(token);
            await transaction.RollbackAsync(token);
        }
        await using var verification = factory.Services.CreateAsyncScope();
        var context = verification.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.False(await context.NotificationJobs.AnyAsync(value => value.NotificationId == scenario.UserAUnread.Id, token));
        Assert.NotEqual("Rolled back", (await context.Users.SingleAsync(value => value.Id == scenario.UserA.Id, token)).FirstName);
    }

    [Fact]
    public async Task DuplicateNotificationJob_IsRejectedByPostgres()
    {
        var token = TestContext.Current.CancellationToken;
        var scenario = await TwoUserNotificationScenarioBuilder.CreateAsync(factory.Services, token);
        await using var scope = factory.Services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        context.NotificationJobs.AddRange(
            new NotificationJob { NotificationId = scenario.UserAUnread.Id, NextAttemptAt = DateTime.UtcNow },
            new NotificationJob { NotificationId = scenario.UserAUnread.Id, NextAttemptAt = DateTime.UtcNow });
        var exception = await Assert.ThrowsAsync<DbUpdateException>(() => context.SaveChangesAsync(token));
        Assert.Equal(PostgresErrorCodes.UniqueViolation, Assert.IsType<PostgresException>(exception.InnerException).SqlState);
    }
}
