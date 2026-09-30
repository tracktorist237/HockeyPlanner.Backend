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
[Trait("Category", "HP79")]
public sealed class TeamApiBaselineTests(HockeyPlannerWebApplicationFactory factory)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AnonymousAndForeignJwt_PrivateReadsCurrentlyDiscloseData_Documents_SEC001(bool anonymous)
    {
        var s = await TeamApiBaselineScenarioBuilder.CreateAsync(factory.Services);
        var p = s.Pair;
        using var client = anonymous ? factory.CreateClient() : AuthenticatedTestClientFactory.Create(factory, p.UserA);
        if (anonymous) await Status(client.GetAsync("/api/auth/me", Ct), 401);
        else Assert.Equal(p.UserA.Id, (await Json(client.GetAsync("/api/auth/me", Ct)))["id"]!.GetValue<Guid>());
        var team = await Json(client.GetAsync($"/api/teams/{p.TeamB.Id}", Ct));
        Assert.Equal(p.TeamB.Id, team["id"]!.GetValue<Guid>());
        Assert.Equal("", team["inviteCode"]!.GetValue<string>());
        Assert.Null(team["myRole"]);
        var members = await Json(client.GetAsync($"/api/teams/{p.TeamB.Id}/members", Ct));
        Assert.Equal(3, members.AsArray().Count);
        Assert.Contains(members.AsArray(), x => x!["userId"]!.GetValue<Guid>() == p.UserB.Id);
        var news = await Json(client.GetAsync($"/api/teams/{p.TeamB.Id}/news", Ct));
        Assert.Equal(s.NewsB.Id, Assert.Single(news.AsArray())!["id"]!.GetValue<Guid>());
        Assert.False(news[0]!["canManage"]!.GetValue<bool>());

        // Query substitution exposes the owner's invite and management projection, even without JWT.
        var spoofed = await Json(client.GetAsync($"/api/teams/{p.TeamB.Id}?currentUserId={p.UserB.Id}", Ct));
        Assert.Equal(p.TeamB.InviteCode, spoofed["inviteCode"]!.GetValue<string>());
        Assert.Equal(1, spoofed["myRole"]!.GetValue<int>());
        var myTeams = await Json(client.GetAsync($"/api/teams?currentUserId={p.UserB.Id}", Ct));
        Assert.Equal(p.TeamB.Id, Assert.Single(myTeams.AsArray())!["id"]!.GetValue<Guid>());
        var feed = await Json(client.GetAsync($"/api/news?currentUserId={p.UserB.Id}", Ct));
        Assert.Equal(s.NewsB.Id, Assert.Single(feed.AsArray())!["id"]!.GetValue<Guid>());
        Assert.True(feed[0]!["canManage"]!.GetValue<bool>());
        var managedNews = await Json(client.GetAsync($"/api/teams/{p.TeamB.Id}/news?currentUserId={p.UserB.Id}", Ct));
        Assert.True(managedNews[0]!["canManage"]!.GetValue<bool>());
    }

    [Fact]
    public async Task PublicDirectory_ExcludesPrivateTeams_AndMemberProjectionHidesInvite()
    {
        var s = await TeamApiBaselineScenarioBuilder.CreateAsync(factory.Services, publicB: true);
        var directory = await Json(factory.Client.GetAsync("/api/teams/public", Ct));
        Assert.DoesNotContain(directory.AsArray(), x => x!["id"]!.GetValue<Guid>() == s.Pair.TeamA.Id);
        var visible = Assert.Single(directory.AsArray(), x => x!["id"]!.GetValue<Guid>() == s.Pair.TeamB.Id)!;
        Assert.Equal("", visible["inviteCode"]!.GetValue<string>());
        using var client = AuthenticatedTestClientFactory.Create(factory, s.Member);
        var team = await Json(client.GetAsync($"/api/teams/{s.Pair.TeamB.Id}?currentUserId={s.Member.Id}", Ct));
        Assert.Equal(3, team["myRole"]!.GetValue<int>());
        Assert.Equal("", team["inviteCode"]!.GetValue<string>());
        await Status(client.GetAsync("/api/teams", Ct), 400);
        await Status(client.GetAsync("/api/news", Ct), 400);
        await Status(client.GetAsync($"/api/teams/{Guid.NewGuid()}", Ct), 404);
    }

    // The same real JWT is used for both own-ID denial and owner-ID success. These are
    // current insecure SEC-001 baselines, deliberately flipped by HP-80, never skipped.
    [Theory]
    [InlineData("update", false)]
    [InlineData("update", true)]
    [InlineData("member-update", false)]
    [InlineData("member-update", true)]
    [InlineData("member-remove", false)]
    [InlineData("member-remove", true)]
    [InlineData("news-create", false)]
    [InlineData("news-create", true)]
    [InlineData("news-update", false)]
    [InlineData("news-update", true)]
    [InlineData("news-delete", false)]
    [InlineData("news-delete", true)]
    public async Task SpoofedCurrentUserId_CurrentlyActsAsOtherOwner_Documents_SEC001(string operation, bool anonymous)
    {
        var s = await TeamApiBaselineScenarioBuilder.CreateAsync(factory.Services);
        using var client = anonymous ? factory.CreateClient() : AuthenticatedTestClientFactory.Create(factory, s.Pair.UserA);
        await Status(Mutate(client, s, operation, s.Pair.UserA.Id), 403);
        await VerifyMutation(s, operation, changed: false);
        await Status(Mutate(client, s, operation, s.Pair.UserB.Id), operation.EndsWith("remove") || operation.EndsWith("delete") ? 204 : 200);
        await VerifyMutation(s, operation, changed: true);
    }

    [Theory]
    [InlineData("owner", 200)]
    [InlineData("admin", 200)]
    [InlineData("member", 403)]
    [InlineData("foreign", 403)]
    public async Task HonestQuery_TeamAndNewsManagement_UsesTeamRole(string actor, int expected)
    {
        var s = await TeamApiBaselineScenarioBuilder.CreateAsync(factory.Services);
        var user = Actor(s, actor);
        using var client = AuthenticatedTestClientFactory.Create(factory, user);
        foreach (var operation in new[] { "update", "news-create", "news-update", "news-delete" })
        {
            await Status(Mutate(client, s, operation, user.Id), expected == 200 && operation == "news-delete" ? 204 : expected);
            await VerifyMutation(s, operation, expected == 200);
        }
    }

    [Theory]
    [InlineData("owner", 200, 204)]
    [InlineData("admin", 403, 204)]
    [InlineData("member", 403, 403)]
    [InlineData("foreign", 403, 403)]
    public async Task MemberRoleChangeAndRemoval_RespectHonestQueryRole(string actor, int updateStatus, int removeStatus)
    {
        var s = await TeamApiBaselineScenarioBuilder.CreateAsync(factory.Services);
        var user = Actor(s, actor);
        using var client = AuthenticatedTestClientFactory.Create(factory, user);
        await Status(client.PutAsJsonAsync($"/api/teams/{s.Pair.TeamB.Id}/members/{s.Member.Id}?currentUserId={user.Id}", new { role = 2 }, Ct), updateStatus);
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            Assert.Equal(updateStatus == 200 ? TeamMemberRole.Admin : TeamMemberRole.Member,
                (await db.TeamMemberships.SingleAsync(x => x.TeamId == s.Pair.TeamB.Id && x.UserId == s.Member.Id, Ct)).Role);
        }
        // Member deleting themself uses the dedicated self-delete guard (400).
        await Status(Mutate(client, s, "member-remove", user.Id), actor == "member" ? 400 : removeStatus);
    }

    [Fact]
    public async Task Admin_CanEditMemberBadge_ButCannotRemoveAdminOrOwner_AndForeignTargetsAre404()
    {
        var s = await TeamApiBaselineScenarioBuilder.CreateAsync(factory.Services);
        using var client = AuthenticatedTestClientFactory.Create(factory, s.Admin);
        var root = $"/api/teams/{s.Pair.TeamB.Id}/members";
        await Status(Mutate(client, s, "member-update", s.Admin.Id), 200);
        await VerifyMutation(s, "member-update", true);
        await Status(client.DeleteAsync($"{root}/{s.Pair.UserB.Id}?currentUserId={s.Admin.Id}", Ct), 400);
        // Use owner JWT with honest admin query to distinguish query authorization from JWT.
        using var owner = AuthenticatedTestClientFactory.Create(factory, s.Pair.UserB);
        await Status(owner.DeleteAsync($"{root}/{s.Admin.Id}?currentUserId={s.Admin.Id}", Ct), 400);
        await Status(owner.PutAsJsonAsync($"{root}/{s.Pair.UserB.Id}?currentUserId={s.Pair.UserB.Id}", new { role = 3 }, Ct), 400);
        await Status(owner.PutAsJsonAsync($"{root}/{s.Member.Id}?currentUserId={s.Pair.UserB.Id}", new { role = 1 }, Ct), 400);
        await Status(owner.PutAsJsonAsync($"{root}/{s.Member.Id}?currentUserId={s.Pair.UserB.Id}", new { role = 2 }, Ct), 200);
        await Status(client.DeleteAsync($"{root}/{s.Member.Id}?currentUserId={s.Admin.Id}", Ct), 403);
        await Status(client.PutAsJsonAsync($"{root}/{s.Pair.UserA.Id}?currentUserId={s.Admin.Id}", new { badgeTitle = "x" }, Ct), 404);
        await Status(client.DeleteAsync($"{root}/{s.Pair.UserA.Id}?currentUserId={s.Admin.Id}", Ct), 404);
        await Status(client.PutAsJsonAsync($"/api/teams/{s.Pair.TeamB.Id}/news/{s.NewsA.Id}?currentUserId={s.Admin.Id}", new { title = "x", body = "x" }, Ct), 404);
        await Status(client.DeleteAsync($"/api/teams/{s.Pair.TeamB.Id}/news/{s.NewsA.Id}?currentUserId={s.Admin.Id}", Ct), 404);
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.Equal(TeamMemberRole.Admin, (await db.TeamMemberships.SingleAsync(x => x.TeamId == s.Pair.TeamB.Id && x.UserId == s.Member.Id, Ct)).Role);
        Assert.Equal(TeamMemberRole.Owner, (await db.TeamMemberships.SingleAsync(x => x.TeamId == s.Pair.TeamB.Id && x.UserId == s.Pair.UserB.Id, Ct)).Role);
        Assert.Equal("News A", (await db.TeamNews.SingleAsync(x => x.Id == s.NewsA.Id, Ct)).Title);
        Assert.True(await db.TeamMemberships.AnyAsync(x => x.TeamId == s.Pair.TeamA.Id && x.UserId == s.Pair.UserA.Id, Ct));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CreateJoinNumberAndLeave_CurrentlyUseSuppliedUser_Documents_SEC001(bool anonymous)
    {
        var s = await TeamApiBaselineScenarioBuilder.CreateAsync(factory.Services, publicB: true);
        var p = s.Pair;
        using var client = anonymous ? factory.CreateClient() : AuthenticatedTestClientFactory.Create(factory, p.UserA);
        var created = await Json(client.PostAsJsonAsync($"/api/teams?currentUserId={p.UserB.Id}", new { name = $"Created {Guid.NewGuid():N}", visibility = 2 }, Ct), 201);
        var createdId = created["id"]!.GetValue<Guid>();
        Assert.Equal(p.UserB.Id, created["createdByUserId"]!.GetValue<Guid>());
        Assert.Equal(1, created["myRole"]!.GetValue<int>());
        // Join A as B (not JWT A); repeat is idempotent.
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            (await db.Teams.FindAsync([p.TeamA.Id], Ct))!.InviteCode = p.TeamA.InviteCode.ToUpperInvariant();
            await db.SaveChangesAsync(Ct);
        }
        for (var i = 0; i < 2; i++) await Status(client.PostAsJsonAsync($"/api/teams/join-by-code?currentUserId={p.UserB.Id}", new { code = p.TeamA.InviteCode }, Ct), 200);
        await Status(client.PutAsJsonAsync($"/api/teams/{p.TeamA.Id}/members/me/number?currentUserId={p.UserB.Id}", new { teamJerseyNumber = 79 }, Ct), 200);
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            Assert.Equal(p.UserB.Id, (await db.TeamMemberships.SingleAsync(x => x.TeamId == createdId, Ct)).UserId);
            Assert.Equal(79, (await db.TeamMemberships.SingleAsync(x => x.TeamId == p.TeamA.Id && x.UserId == p.UserB.Id, Ct)).TeamJerseyNumber);
        }
        await Status(client.DeleteAsync($"/api/teams/{p.TeamA.Id}/members/me?currentUserId={p.UserB.Id}", Ct), 204);
        await Status(client.PostAsync($"/api/teams/{p.TeamA.Id}/join-public?currentUserId={p.UserB.Id}", null, Ct), 400);
        await Status(client.DeleteAsync($"/api/teams/{p.TeamB.Id}/members/me?currentUserId={s.Member.Id}", Ct), 204);
        await Status(client.PostAsync($"/api/teams/{p.TeamB.Id}/join-public?currentUserId={s.Member.Id}&teamJerseyNumber=12", null, Ct), 200);
        await using var verify = factory.Services.CreateAsyncScope();
        var context = verify.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.False(await context.TeamMemberships.AnyAsync(x => x.TeamId == p.TeamA.Id && x.UserId == p.UserB.Id, Ct));
        Assert.Equal(12, (await context.TeamMemberships.SingleAsync(x => x.TeamId == p.TeamB.Id && x.UserId == s.Member.Id, Ct)).TeamJerseyNumber);
    }

    [Fact]
    public async Task OwnerLeave_WithOthersIs400_ButLastOwnerCurrentlyLeavesOwnerlessTeam_Documents_TECH001()
    {
        var s = await TeamApiBaselineScenarioBuilder.CreateAsync(factory.Services);
        using var client = AuthenticatedTestClientFactory.Create(factory, s.Pair.UserB);
        await Status(client.DeleteAsync($"/api/teams/{s.Pair.TeamB.Id}/members/me?currentUserId={s.Pair.UserB.Id}", Ct), 400);
        using var ownerA = AuthenticatedTestClientFactory.Create(factory, s.Pair.UserA);
        await Status(ownerA.DeleteAsync($"/api/teams/{s.Pair.TeamA.Id}/members/me?currentUserId={s.Pair.UserA.Id}", Ct), 204);
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.True(await db.Teams.AnyAsync(x => x.Id == s.Pair.TeamA.Id, Ct));
        Assert.False(await db.TeamMemberships.AnyAsync(x => x.TeamId == s.Pair.TeamA.Id, Ct));
        Assert.Equal(3, await db.TeamMemberships.CountAsync(x => x.TeamId == s.Pair.TeamB.Id, Ct));
    }

    internal static User Actor(TeamApiBaselineScenario s, string actor) => actor switch
    { "owner" => s.Pair.UserB, "admin" => s.Admin, "member" => s.Member, _ => s.Pair.UserA };

    private static Task<HttpResponseMessage> Mutate(HttpClient client, TeamApiBaselineScenario s, string operation, Guid actor)
    {
        var root = $"/api/teams/{s.Pair.TeamB.Id}";
        var query = $"?currentUserId={actor}";
        return operation switch
        {
            "update" => client.PutAsJsonAsync(root + query, new { name = s.Pair.TeamB.Name, description = "Changed", visibility = 2 }, Ct),
            "member-update" => client.PutAsJsonAsync($"{root}/members/{s.Member.Id}{query}", new { badgeTitle = "Changed" }, Ct),
            "member-remove" => client.DeleteAsync($"{root}/members/{s.Member.Id}{query}", Ct),
            "news-create" => client.PostAsJsonAsync(root + "/news" + query, new { title = "Created", body = "Synthetic", sendNotification = false }, Ct),
            "news-update" => client.PutAsJsonAsync($"{root}/news/{s.NewsB.Id}{query}", new { title = "Changed", body = "Synthetic" }, Ct),
            "news-delete" => client.DeleteAsync($"{root}/news/{s.NewsB.Id}{query}", Ct),
            _ => throw new ArgumentOutOfRangeException(nameof(operation))
        };
    }

    private async Task VerifyMutation(TeamApiBaselineScenario s, string operation, bool changed)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        switch (operation)
        {
            case "update": Assert.Equal(changed ? "Changed" : null, (await db.Teams.SingleAsync(x => x.Id == s.Pair.TeamB.Id, Ct)).Description); break;
            case "member-update": Assert.Equal(changed ? "Changed" : null, (await db.TeamMemberships.SingleAsync(x => x.TeamId == s.Pair.TeamB.Id && x.UserId == s.Member.Id, Ct)).BadgeTitle); break;
            case "member-remove": Assert.Equal(!changed, await db.TeamMemberships.AnyAsync(x => x.TeamId == s.Pair.TeamB.Id && x.UserId == s.Member.Id, Ct)); break;
            case "news-create": Assert.Equal(changed ? 1 : 0, await db.TeamNews.CountAsync(x => x.TeamId == s.Pair.TeamB.Id && x.Title == "Created", Ct)); break;
            case "news-update": Assert.Equal(changed ? "Changed" : "News B", (await db.TeamNews.SingleAsync(x => x.Id == s.NewsB.Id, Ct)).Title); break;
            case "news-delete": Assert.Equal(!changed, await db.TeamNews.AnyAsync(x => x.Id == s.NewsB.Id, Ct)); break;
        }
        Assert.Equal("News A", (await db.TeamNews.SingleAsync(x => x.Id == s.NewsA.Id, Ct)).Title);
    }

    internal static async Task Status(Task<HttpResponseMessage> pending, int status)
    {
        using var response = await pending;
        Assert.Equal(status, (int)response.StatusCode);
    }

    internal static async Task<JsonNode> Json(Task<HttpResponseMessage> pending, int status = 200)
    {
        using var response = await pending;
        Assert.Equal(status, (int)response.StatusCode);
        return JsonNode.Parse(await response.Content.ReadAsStringAsync(Ct))!;
    }
}
