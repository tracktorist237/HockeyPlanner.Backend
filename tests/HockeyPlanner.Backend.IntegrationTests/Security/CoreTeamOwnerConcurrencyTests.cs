using System.Collections.Concurrent;
using System.Data.Common;
using System.Net.Http.Json;
using HockeyPlanner.Backend.Core.Entities;
using HockeyPlanner.Backend.Core.Enums;
using HockeyPlanner.Backend.Infrastructure.Data;
using HockeyPlanner.Backend.IntegrationTests.Fixtures;
using HockeyPlanner.Backend.IntegrationTests.Infrastructure;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;

namespace HockeyPlanner.Backend.IntegrationTests.Security;

[Collection(IntegrationTestCollection.Name)]
[Trait("Category", "HP81")]
public sealed class CoreTeamOwnerConcurrencyTests(HockeyPlannerWebApplicationFactory factory)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData("last-owner")]
    [InlineData("last-member-leaves")]
    [InlineData("mixed-mutations")]
    public async Task OverlappingRealHttpMutations_CannotRemoveOrReplaceOriginalOwner(string scenario)
    {
        var seed = await TeamApiBaselineScenarioBuilder.CreateAsync(factory.Services, publicB: true);
        var team = scenario == "last-owner" ? seed.Pair.TeamA : seed.Pair.TeamB;
        var owner = scenario == "last-owner" ? seed.Pair.UserA : seed.Pair.UserB;
        if (scenario == "last-member-leaves")
        {
            await using var scope = factory.Services.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.TeamMemberships.Remove(await db.TeamMemberships.SingleAsync(x => x.TeamId == team.Id && x.UserId == seed.Admin.Id, Ct));
            await db.SaveChangesAsync(Ct);
        }
        Guid ownerMembershipId;
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            ownerMembershipId = (await db.TeamMemberships.AsNoTracking().SingleAsync(x => x.TeamId == team.Id && x.UserId == owner.Id, Ct)).Id;
        }

        var count = scenario == "mixed-mutations" ? 6 : scenario == "last-owner" ? 3 : 2;
        var barrier = new MembershipReadBarrier(count);
        using var host = factory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
            services.AddDbContext<AppDbContext>(options => options.AddInterceptors(barrier))));
        using var ownerClient = Client(owner);
        using var adminClient = Client(seed.Admin);
        using var memberClient = Client(seed.Member);
        var root = $"/api/teams/{team.Id}/members";
        var requests = new List<(Task<HttpResponseMessage> Response, int Status)>
        {
            (ownerClient.DeleteAsync($"{root}/me", Ct), 400)
        };
        if (scenario == "last-member-leaves")
            requests.Add((memberClient.DeleteAsync($"{root}/me", Ct), 204));
        else
        {
            requests.Add((ownerClient.PutAsJsonAsync($"{root}/{owner.Id}", new { role = 3 }, Ct), 400));
            if (scenario == "last-owner")
                requests.Add((ownerClient.DeleteAsync($"{root}/me", Ct), 400));
            else
            {
                // Admin is a distinct actor: this reaches Owner removal, not the self-delete guard.
                requests.Add((adminClient.DeleteAsync($"{root}/{owner.Id}", Ct), 400));
                requests.Add((ownerClient.PutAsJsonAsync($"{root}/{seed.Admin.Id}", new { role = 1 }, Ct), 400));
                requests.Add((memberClient.DeleteAsync($"{root}/me", Ct), 204));
                requests.Add((ownerClient.PutAsJsonAsync($"{root}/{seed.Admin.Id}", new { badgeTitle = "Concurrent badge", teamJerseyNumber = 42 }, Ct), 200));
            }
        }
        await Task.WhenAll(requests.Select(x => x.Response));
        Assert.Equal(count, barrier.Arrivals);
        foreach (var request in requests)
        {
            using var response = await request.Response;
            Assert.Equal(request.Status, (int)response.StatusCode);
        }

        // Every assertion reads from a fresh DbContext, never tracked seed entities.
        await using var verify = factory.Services.CreateAsyncScope();
        var persisted = verify.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.True(await persisted.Teams.AsNoTracking().AnyAsync(x => x.Id == team.Id, Ct));
        var memberships = await persisted.TeamMemberships.AsNoTracking().Where(x => x.TeamId == team.Id).ToListAsync(Ct);
        var stillOwner = Assert.Single(memberships, x => x.Role == TeamMemberRole.Owner);
        Assert.Equal(owner.Id, stillOwner.UserId);
        Assert.Equal(ownerMembershipId, stillOwner.Id);
        Assert.Null(stillOwner.BadgeTitle);
        Assert.Null(stillOwner.TeamJerseyNumber);
        if (scenario != "last-owner") Assert.DoesNotContain(memberships, x => x.UserId == seed.Member.Id);
        if (scenario == "mixed-mutations")
        {
            var admin = Assert.Single(memberships, x => x.UserId == seed.Admin.Id);
            Assert.Equal(TeamMemberRole.Admin, admin.Role);
            Assert.Equal("Concurrent badge", admin.BadgeTitle);
            Assert.Equal(42, admin.TeamJerseyNumber);
        }
        else Assert.Single(memberships);

        HttpClient Client(User user)
        {
            var client = host.CreateClient();
            using var signed = AuthenticatedTestClientFactory.Create(factory, user);
            client.DefaultRequestHeaders.Authorization = signed.DefaultRequestHeaders.Authorization;
            return client;
        }
    }

    // Hold each request AFTER its first real PostgreSQL membership read and before
    // it can apply the policy/write. No request can finish until all have arrived.
    // A timeout fails the test if setup accidentally serializes the requests.
    private sealed class MembershipReadBarrier(int participants) : DbCommandInterceptor
    {
        private readonly ConcurrentDictionary<Guid, byte> _contexts = new();
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _arrivals;
        public int Arrivals => _arrivals;

        public override async ValueTask<DbDataReader> ReaderExecutedAsync(DbCommand command,
            CommandExecutedEventData eventData, DbDataReader result, CancellationToken cancellationToken = default)
        {
            if (command.CommandText.StartsWith("SELECT", StringComparison.Ordinal)
                && command.CommandText.Contains("FROM team_memberships", StringComparison.Ordinal)
                && eventData.Context is { } context && _contexts.TryAdd(context.ContextId.InstanceId, 0))
            {
                if (Interlocked.Increment(ref _arrivals) == participants) _release.TrySetResult();
                await _release.Task.WaitAsync(TimeSpan.FromSeconds(20), cancellationToken);
            }
            return result;
        }
    }
}
