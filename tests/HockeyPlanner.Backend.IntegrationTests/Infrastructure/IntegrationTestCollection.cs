namespace HockeyPlanner.Backend.IntegrationTests.Infrastructure;

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class IntegrationTestCollection : ICollectionFixture<HockeyPlannerWebApplicationFactory>
{
    public const string Name = "PostgreSQL integration tests";
}
