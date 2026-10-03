using System.Net.Http.Json;
using System.Text.Json.Nodes;
using HockeyPlanner.Backend.Core.Entities;
using HockeyPlanner.Backend.Core.Enums;
using HockeyPlanner.Backend.Infrastructure.Data;
using HockeyPlanner.Backend.IntegrationTests.Fixtures;
using HockeyPlanner.Backend.IntegrationTests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace HockeyPlanner.Backend.IntegrationTests.Security;

[Collection(IntegrationTestCollection.Name)]
[Trait("Category", "HP81")]
public sealed class CoreTeamUseCaseTests(HockeyPlannerWebApplicationFactory factory)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Create_NormalizesFields_ReturnsLocation_AndPersistsOwner()
    {
        var seed = await TeamApiBaselineScenarioBuilder.CreateAsync(factory.Services);
        using var client = AuthenticatedTestClientFactory.Create(factory, seed.Pair.UserA);
        var suffix = Guid.NewGuid().ToString("N");
        using var response = await client.PostAsJsonAsync("/api/teams", new
        {
            name = $"  New   {suffix}  ", description = "  Team   description ", visibility = 1,
            phones = new[] { new { title = " Phone   contact ", value = " 123 " }, new { title = "", value = "discard" } },
            blockedJerseyNumbers = new[] { 9, 0, 9 }
        }, Ct);
        Assert.Equal(201, (int)response.StatusCode);
        var dto = JsonNode.Parse(await response.Content.ReadAsStringAsync(Ct))!;
        var id = dto["id"]!.GetValue<Guid>();
        Assert.EndsWith($"/api/teams/{id}", response.Headers.Location!.ToString());
        Assert.Equal($"New {suffix}", dto["name"]!.GetValue<string>());
        Assert.Equal(1, dto["myRole"]!.GetValue<int>());
        Assert.Equal(1, dto["membersCount"]!.GetValue<int>());
        Assert.Equal("Phone contact", dto["phones"]![0]!["title"]!.GetValue<string>());
        Assert.Equal("123", dto["phones"]![0]!["value"]!.GetValue<string>());
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var team = await db.Teams.AsNoTracking().SingleAsync(x => x.Id == id, Ct);
        var membership = await db.TeamMemberships.AsNoTracking().SingleAsync(x => x.TeamId == id, Ct);
        Assert.Equal(seed.Pair.UserA.Id, membership.UserId);
        Assert.Equal(TeamMemberRole.Owner, membership.Role);
        Assert.Equal("Team description", team.Description);
        Assert.Equal("[0,9]", team.BlockedJerseyNumbersJson);
        await Status(client.PostAsJsonAsync("/api/teams", new { name = $"new {suffix}" }, Ct), 409);
        await Status(client.PostAsJsonAsync("/api/teams", new { name = $"Invalid {suffix}", blockedJerseyNumbers = new[] { 100 } }, Ct), 400);
        using var missingUser = AuthenticatedTestClientFactory.Create(factory, new User { FirstName = "Missing" });
        await Status(missingUser.PostAsJsonAsync("/api/teams", new { name = $"Missing {suffix}" }, Ct), 404);
    }

    [Theory]
    [InlineData("duplicate-name", 409)]
    [InlineData("invalid-blocked", 400)]
    [InlineData("duplicate-assigned", 409)]
    [InlineData("blocked-assigned", 409)]
    public async Task Update_ValidationErrors_DoNotPersistPartialChanges(string error, int status)
    {
        var seed = await TeamApiBaselineScenarioBuilder.CreateAsync(factory.Services);
        var id = seed.Pair.TeamB.Id;
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var members = await db.TeamMemberships.Where(x => x.TeamId == id).ToListAsync(Ct);
            foreach (var member in members) member.TeamJerseyNumber = 17;
            await db.SaveChangesAsync(Ct);
        }
        using var owner = AuthenticatedTestClientFactory.Create(factory, seed.Pair.UserB);
        await Status(owner.PutAsJsonAsync($"/api/teams/{id}", new
        {
            name = error == "duplicate-name" ? seed.Pair.TeamA.Name : $"Attempt {Guid.NewGuid():N}",
            description = "Must not persist", allowDuplicateJerseyNumbers = error != "duplicate-assigned",
            blockedJerseyNumbers = error == "invalid-blocked" ? new[] { -1 } : error == "blocked-assigned" ? new[] { 17 } : Array.Empty<int>()
        }, Ct), status);
        await using var verify = factory.Services.CreateAsyncScope();
        var persisted = await verify.ServiceProvider.GetRequiredService<AppDbContext>().Teams.AsNoTracking().SingleAsync(x => x.Id == id, Ct);
        Assert.Equal(seed.Pair.TeamB.Name, persisted.Name);
        Assert.NotEqual("Must not persist", persisted.Description);
        Assert.True(persisted.AllowDuplicateJerseyNumbers);
    }

    [Theory]
    [InlineData(TeamMemberRole.Owner)]
    [InlineData(TeamMemberRole.Admin)]
    public async Task AdminMetadata_OnOwnerAndAdmin_PreservesExplicitlyApprovedBaseBehavior(TeamMemberRole targetRole)
    {
        var seed = await TeamApiBaselineScenarioBuilder.CreateAsync(factory.Services);
        var target = targetRole == TeamMemberRole.Owner ? seed.Pair.UserB : seed.Admin;
        using var admin = AuthenticatedTestClientFactory.Create(factory, seed.Admin);
        var root = $"/api/teams/{seed.Pair.TeamB.Id}/members/{target.Id}";
        await Status(admin.PutAsJsonAsync(root, new { badgeTitle = "  Team   captain ", teamJerseyNumber = 99 }, Ct), 200);
        await Status(admin.PutAsJsonAsync(root, new { role = 3, teamJerseyNumber = 99 }, Ct), 403);
        await using var verify = factory.Services.CreateAsyncScope();
        var persisted = await verify.ServiceProvider.GetRequiredService<AppDbContext>().TeamMemberships.AsNoTracking()
            .SingleAsync(x => x.TeamId == seed.Pair.TeamB.Id && x.UserId == target.Id, Ct);
        Assert.Equal(targetRole, persisted.Role);
        Assert.Equal("Team captain", persisted.BadgeTitle);
        Assert.Equal(99, persisted.TeamJerseyNumber);
    }

    [Theory]
    [InlineData("code")]
    [InlineData("public")]
    public async Task Join_NormalizesCode_AndRepeatedMembershipIsIdempotent(string path)
    {
        var seed = await TeamApiBaselineScenarioBuilder.CreateAsync(factory.Services, publicB: true);
        using var actor = AuthenticatedTestClientFactory.Create(factory, seed.Pair.UserA);
        for (var i = 0; i < 2; i++)
        {
            using var response = path == "code"
                ? await actor.PostAsJsonAsync("/api/teams/join-by-code", new { code = " " + seed.Pair.TeamB.InviteCode.ToLowerInvariant() + " ", teamJerseyNumber = i == 0 ? 0 : 99 }, Ct)
                : await actor.PostAsync($"/api/teams/{seed.Pair.TeamB.Id}/join-public?teamJerseyNumber={(i == 0 ? 0 : 99)}", null, Ct);
            Assert.Equal(200, (int)response.StatusCode);
            var dto = JsonNode.Parse(await response.Content.ReadAsStringAsync(Ct))!;
            Assert.Equal(3, dto["myRole"]!.GetValue<int>());
            Assert.Equal(0, dto["myTeamJerseyNumber"]!.GetValue<int>());
            Assert.Equal("", dto["inviteCode"]!.GetValue<string>());
            await using var scope = factory.Services.CreateAsyncScope();
            var members = await scope.ServiceProvider.GetRequiredService<AppDbContext>().TeamMemberships.AsNoTracking()
                .Where(x => x.TeamId == seed.Pair.TeamB.Id).ToListAsync(Ct);
            Assert.Equal(4, members.Count);
            Assert.Equal(TeamMemberRole.Member, members.Single(x => x.UserId == seed.Pair.UserA.Id).Role);
            Assert.Equal(0, members.Single(x => x.UserId == seed.Pair.UserA.Id).TeamJerseyNumber);
        }
        await Status(actor.PostAsJsonAsync("/api/teams/join-by-code", new { code = "UNKNOWNCODE" }, Ct), 404);
        await Status(actor.PostAsync($"/api/teams/{seed.Pair.TeamA.Id}/join-public", null, Ct), 400);
    }

    [Theory]
    [InlineData("self", null, 409)]
    [InlineData("self", -1, 409)]
    [InlineData("self", 100, 409)]
    [InlineData("self", 13, 409)]
    [InlineData("self", 17, 409)]
    [InlineData("self", 0, 200)]
    [InlineData("self", 99, 200)]
    [InlineData("member", 17, 409)]
    [InlineData("code", 17, 409)]
    [InlineData("public", 13, 409)]
    public async Task JerseyRules_PreserveRangeRequiredBlockedAndConflictStatuses(string operation, int? number, int status)
    {
        var seed = await TeamApiBaselineScenarioBuilder.CreateAsync(factory.Services, publicB: true);
        var id = seed.Pair.TeamB.Id;
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var team = await db.Teams.SingleAsync(x => x.Id == id, Ct);
            team.AllowDuplicateJerseyNumbers = false;
            team.BlockedJerseyNumbersJson = "[13]";
            (await db.TeamMemberships.SingleAsync(x => x.TeamId == id && x.UserId == seed.Pair.UserB.Id, Ct)).TeamJerseyNumber = 17;
            await db.SaveChangesAsync(Ct);
        }
        using var actor = AuthenticatedTestClientFactory.Create(factory, operation == "member" ? seed.Pair.UserB : operation == "self" ? seed.Member : seed.Pair.UserA);
        var request = operation switch
        {
            "self" => actor.PutAsJsonAsync($"/api/teams/{id}/members/me/number", new { teamJerseyNumber = number }, Ct),
            "member" => actor.PutAsJsonAsync($"/api/teams/{id}/members/{seed.Member.Id}", new { teamJerseyNumber = number }, Ct),
            "code" => actor.PostAsJsonAsync("/api/teams/join-by-code", new { code = seed.Pair.TeamB.InviteCode, teamJerseyNumber = number }, Ct),
            _ => actor.PostAsync($"/api/teams/{id}/join-public?teamJerseyNumber={number}", null, Ct)
        };
        await Status(request, status);
        await using var verify = factory.Services.CreateAsyncScope();
        var dbVerify = verify.ServiceProvider.GetRequiredService<AppDbContext>();
        var member = await dbVerify.TeamMemberships.AsNoTracking().SingleAsync(x => x.TeamId == id && x.UserId == seed.Member.Id, Ct);
        Assert.Equal(status == 200 ? number : null, member.TeamJerseyNumber);
        Assert.False(await dbVerify.TeamMemberships.AnyAsync(x => x.TeamId == id && x.UserId == seed.Pair.UserA.Id, Ct));
    }

    [Fact]
    public async Task SelfJersey_NullAllowed_NonMember404_AndOwnerProjectionRetained()
    {
        var seed = await TeamApiBaselineScenarioBuilder.CreateAsync(factory.Services);
        using var owner = AuthenticatedTestClientFactory.Create(factory, seed.Pair.UserB);
        using var response = await owner.PutAsJsonAsync($"/api/teams/{seed.Pair.TeamB.Id}/members/me/number", new { teamJerseyNumber = (int?)null }, Ct);
        Assert.Equal(200, (int)response.StatusCode);
        var dto = JsonNode.Parse(await response.Content.ReadAsStringAsync(Ct))!;
        Assert.Equal(1, dto["myRole"]!.GetValue<int>());
        Assert.Equal(seed.Pair.TeamB.InviteCode, dto["inviteCode"]!.GetValue<string>());
        Assert.Null(dto["myTeamJerseyNumber"]);
        using var foreign = AuthenticatedTestClientFactory.Create(factory, seed.Pair.UserA);
        await Status(foreign.PutAsJsonAsync($"/api/teams/{seed.Pair.TeamB.Id}/members/me/number", new { teamJerseyNumber = 12 }, Ct), 404);
    }

    private static async Task Status(Task<HttpResponseMessage> request, int status)
    {
        using var response = await request;
        Assert.Equal(status, (int)response.StatusCode);
    }
}
