using System.Net;
using HockeyPlanner.Backend.Infrastructure.Data;
using HockeyPlanner.Backend.IntegrationTests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace HockeyPlanner.Backend.IntegrationTests.Smoke;

[Collection(IntegrationTestCollection.Name)]
public sealed class TestInfrastructureSmokeTests(
    HockeyPlannerWebApplicationFactory factory,
    ITestOutputHelper output)
{
    [Fact]
    public async Task HealthEndpoint_StartsTestHostAndReturnsOk()
    {
        var response = await factory.Client.GetAsync("/health", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task AppDbContext_UsesContainerDatabaseWithCurrentSchema()
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        Assert.Equal("Npgsql.EntityFrameworkCore.PostgreSQL", dbContext.Database.ProviderName);

        await dbContext.Database.OpenConnectionAsync(TestContext.Current.CancellationToken);

        await using var databaseCommand = dbContext.Database.GetDbConnection().CreateCommand();
        databaseCommand.CommandText = "SELECT current_database()";
        var actualDatabaseName = (string?)await databaseCommand.ExecuteScalarAsync(TestContext.Current.CancellationToken);

        Assert.Equal(factory.DatabaseName, actualDatabaseName);
        Assert.StartsWith("hockeyplanner_test_", actualDatabaseName, StringComparison.Ordinal);
        output.WriteLine($"Integration PostgreSQL database: {actualDatabaseName}");
        var effectiveConnectionString = new NpgsqlConnectionStringBuilder(
            dbContext.Database.GetDbConnection().ConnectionString);
        Assert.Equal(factory.MappedPostgreSqlPort, effectiveConnectionString.Port);

        await using var schemaCommand = dbContext.Database.GetDbConnection().CreateCommand();
        schemaCommand.CommandText = "SELECT to_regclass('public.users') IS NOT NULL";
        var usersTableExists = (bool?)await schemaCommand.ExecuteScalarAsync(TestContext.Current.CancellationToken);

        Assert.True(usersTableExists);
    }
}
