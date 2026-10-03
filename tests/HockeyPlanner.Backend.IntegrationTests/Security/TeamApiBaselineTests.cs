using System.Net.Http.Json;
using System.IdentityModel.Tokens.Jwt;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Text;
using System.Text.Json.Nodes;
using HockeyPlanner.Backend.Core.Entities;
using HockeyPlanner.Backend.Core.Enums;
using HockeyPlanner.Backend.Infrastructure.Data;
using HockeyPlanner.Backend.IntegrationTests.Fixtures;
using HockeyPlanner.Backend.IntegrationTests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using HockeyPlanner.Backend.WebAPI.Options;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

namespace HockeyPlanner.Backend.IntegrationTests.Security;

[Collection(IntegrationTestCollection.Name)]
[Trait("Category", "HP79")]
public sealed class TeamApiBaselineTests(HockeyPlannerWebApplicationFactory factory)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData("missing")]
    [InlineData("malformed")]
    [InlineData("conflicting")]
    [InlineData("empty")]
    public async Task SignedJwtWithoutCanonicalIdentity_CannotFallBackToOwnerQuery(string identity)
    {
        var s = await TeamApiBaselineScenarioBuilder.CreateAsync(factory.Services, publicB: true);
        using var client = factory.CreateClient();
        using (var scope = factory.Services.CreateScope())
        {
            var options = scope.ServiceProvider.GetRequiredService<IOptions<JwtOptions>>().Value;
            var claims = new List<Claim>();
            if (identity != "missing")
            {
                claims.Add(new Claim(JwtRegisteredClaimNames.Sub, identity switch
                {
                    "malformed" => "not-a-guid", "empty" => Guid.Empty.ToString(), _ => s.Pair.UserA.Id.ToString()
                }));
                if (identity != "empty") claims.Add(new Claim("nameid", s.Pair.UserB.Id.ToString()));
            }
            var token = new JwtSecurityToken(options.Issuer, options.Audience, claims,
                DateTime.UtcNow.AddMinutes(-1), DateTime.UtcNow.AddMinutes(5),
                new SigningCredentials(new SymmetricSecurityKey(Encoding.UTF8.GetBytes(options.SigningKey)), SecurityAlgorithms.HmacSha256));
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", new JwtSecurityTokenHandler().WriteToken(token));
        }
        var query = $"?currentUserId={s.Pair.UserB.Id}";
        await Status(client.GetAsync("/api/teams" + query, Ct), 401);
        await Status(client.GetAsync("/api/news" + query, Ct), 401);
        foreach (var suffix in new[] { "", "/members", "/news" })
            await Status(client.GetAsync($"/api/teams/{s.Pair.TeamB.Id}{suffix}{query}", Ct), 401);
        foreach (var operation in new[] { "update", "member-update", "member-remove", "news-create", "news-update", "news-delete" })
        {
            await Status(Mutate(client, s, operation, s.Pair.UserB.Id), 401);
            await VerifyMutation(s, operation, false);
        }
    }

    // HP-80: real JWTs cover private/public visibility and ignored legacy actor queries.
    [Theory]
    [InlineData(false, "anonymous", 401)]
    [InlineData(false, "foreign", 403)]
    [InlineData(false, "owner", 200)]
    [InlineData(false, "admin", 200)]
    [InlineData(false, "member", 200)]
    [InlineData(true, "anonymous", 200)]
    [InlineData(true, "foreign", 200)]
    [InlineData(true, "owner", 200)]
    [InlineData(true, "admin", 200)]
    [InlineData(true, "member", 200)]
    public async Task TeamReads_VisibilityAndProjectionsUseJwtOnly(bool publicTeam, string actor, int expected)
    {
        var s = await TeamApiBaselineScenarioBuilder.CreateAsync(factory.Services, publicTeam);
        var p = s.Pair;
        var user = Actor(s, actor);
        await using (var setup = factory.Services.CreateAsyncScope())
        {
            var db = setup.ServiceProvider.GetRequiredService<AppDbContext>();
            var membership = await db.TeamMemberships.SingleOrDefaultAsync(x => x.TeamId == p.TeamB.Id && x.UserId == user.Id, Ct);
            if (membership != null)
            {
                membership.BadgeTitle = actor + " badge";
                membership.TeamJerseyNumber = 17;
                await db.SaveChangesAsync(Ct);
            }
        }
        using var client = actor == "anonymous" ? factory.CreateClient() : AuthenticatedTestClientFactory.Create(factory, user);
        if (actor == "anonymous") await Status(client.GetAsync("/api/auth/me", Ct), 401);
        else Assert.Equal(user.Id, (await Json(client.GetAsync("/api/auth/me", Ct)))["id"]!.GetValue<Guid>());
        foreach (var query in new[] { "", $"?currentUserId={p.UserB.Id}", "?currentUserId=not-a-guid" })
        {
            foreach (var suffix in new[] { "", "/members", "/news" })
            {
                var url = $"/api/teams/{p.TeamB.Id}{suffix}{query}";
                if (expected != 200) { await Status(client.GetAsync(url, Ct), expected); continue; }
                var body = await Json(client.GetAsync(url, Ct));
                var member = actor is "owner" or "admin" or "member";
                var manager = actor is "owner" or "admin";
                if (suffix == "")
                {
                    Assert.Equal(p.TeamB.Id, body["id"]!.GetValue<Guid>());
                    Assert.Equal(manager ? p.TeamB.InviteCode : "", body["inviteCode"]!.GetValue<string>());
                    Assert.Equal(member ? actor switch { "owner" => 1, "admin" => 2, _ => 3 } : (int?)null, body["myRole"]?.GetValue<int>());
                    Assert.Equal(member ? actor + " badge" : null, body["myBadgeTitle"]?.GetValue<string>());
                    Assert.Equal(member ? 17 : (int?)null, body["myTeamJerseyNumber"]?.GetValue<int>());
                }
                else if (suffix == "/news")
                {
                    Assert.Equal(s.NewsB.Id, Assert.Single(body.AsArray())!["id"]!.GetValue<Guid>());
                    Assert.Equal(manager, body[0]!["canManage"]!.GetValue<bool>());
                }
                else
                {
                    Assert.Equal(3, body.AsArray().Count);
                    Assert.Equal(new[] { p.UserB.Id, s.Admin.Id, s.Member.Id }.OrderBy(x => x),
                        body.AsArray().Select(x => x!["userId"]!.GetValue<Guid>()).OrderBy(x => x));
                }
            }
        }
        foreach (var suffix in new[] { "", "/members", "/news" })
            await Status(client.GetAsync($"/api/teams/{Guid.NewGuid()}{suffix}?currentUserId={p.UserB.Id}", Ct), 404);
    }

    [Theory]
    [InlineData("anonymous")]
    [InlineData("foreign")]
    [InlineData("owner")]
    [InlineData("admin")]
    [InlineData("member")]
    public async Task MyTeamsAndFeed_FilterByJwt_NotSpoofedOwner(string actor)
    {
        var s = await TeamApiBaselineScenarioBuilder.CreateAsync(factory.Services);
        var user = Actor(s, actor);
        using var client = actor == "anonymous" ? factory.CreateClient() : AuthenticatedTestClientFactory.Create(factory, user);
        foreach (var query in new[] { "", $"?currentUserId={s.Pair.UserB.Id}", "?currentUserId=invalid" })
        {
            if (actor == "anonymous")
            {
                await Status(client.GetAsync("/api/teams" + query, Ct), 401);
                await Status(client.GetAsync("/api/news" + query, Ct), 401);
                continue;
            }
            var teamId = actor == "foreign" ? s.Pair.TeamA.Id : s.Pair.TeamB.Id;
            var newsId = actor == "foreign" ? s.NewsA.Id : s.NewsB.Id;
            var teams = await Json(client.GetAsync("/api/teams" + query, Ct));
            Assert.Equal(teamId, Assert.Single(teams.AsArray())!["id"]!.GetValue<Guid>());
            var news = await Json(client.GetAsync("/api/news" + query, Ct));
            Assert.Equal(newsId, Assert.Single(news.AsArray())!["id"]!.GetValue<Guid>());
            Assert.Equal(actor != "member", news[0]!["canManage"]!.GetValue<bool>());
        }
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
        await Status(client.GetAsync("/api/teams", Ct), 200);
        await Status(client.GetAsync("/api/news", Ct), 200);
        await Status(client.GetAsync($"/api/teams/{Guid.NewGuid()}", Ct), 404);
        // The logo route remains anonymous even for a private team's missing logo.
        await Status(factory.Client.GetAsync($"/api/teams/{s.Pair.TeamA.Id}/pwa-logo", Ct), 404);
    }

    // The legacy query is ignored for both anonymous and real foreign JWT requests.
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
    public async Task SpoofedCurrentUserId_CannotActAsOtherOwner_SEC001Regression(string operation, bool anonymous)
    {
        var s = await TeamApiBaselineScenarioBuilder.CreateAsync(factory.Services);
        using var client = anonymous ? factory.CreateClient() : AuthenticatedTestClientFactory.Create(factory, s.Pair.UserA);
        await Status(Mutate(client, s, operation, s.Pair.UserA.Id), anonymous ? 401 : 403);
        await VerifyMutation(s, operation, changed: false);
        await Status(Mutate(client, s, operation, s.Pair.UserB.Id), anonymous ? 401 : 403);
        await VerifyMutation(s, operation, changed: false);
    }

    [Theory]
    [InlineData("owner", 200)]
    [InlineData("admin", 200)]
    [InlineData("member", 403)]
    [InlineData("foreign", 403)]
    public async Task TeamAndNewsManagement_UsesJwtTeamRoleDespiteForeignQuery(string actor, int expected)
    {
        var s = await TeamApiBaselineScenarioBuilder.CreateAsync(factory.Services);
        var user = Actor(s, actor);
        using var client = AuthenticatedTestClientFactory.Create(factory, user);
        foreach (var operation in new[] { "update", "news-create", "news-update", "news-delete" })
        {
            await Status(Mutate(client, s, operation, s.Pair.UserA.Id), expected == 200 && operation == "news-delete" ? 204 : expected);
            await VerifyMutation(s, operation, expected == 200);
            if (operation == "news-create" && expected == 200)
            {
                await using var scope = factory.Services.CreateAsyncScope();
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                Assert.Equal(user.Id, (await db.TeamNews.SingleAsync(x => x.TeamId == s.Pair.TeamB.Id && x.Title == "Created", Ct)).AuthorUserId);
            }
        }
    }

    [Theory]
    [InlineData("owner", 200, 204)]
    [InlineData("admin", 403, 204)]
    [InlineData("member", 403, 403)]
    [InlineData("foreign", 403, 403)]
    public async Task MemberRoleChangeAndRemoval_RespectJwtRole(string actor, int updateStatus, int removeStatus)
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
        await VerifyMutation(s, "member-remove", actor != "member" && removeStatus == 204);
    }

    [Fact]
    public async Task Member_WithHonestActor_CannotEditOrRemoveAnotherMember_CurrentBaseline()
    {
        var s = await TeamApiBaselineScenarioBuilder.CreateAsync(factory.Services);
        var peer = new User { FirstName = "Peer", LastName = "Baseline", EmailConfirmed = true };
        var membership = new TeamMembership
        {
            TeamId = s.Pair.TeamB.Id, UserId = peer.Id, Role = TeamMemberRole.Member,
            BadgeTitle = "Original badge", TeamJerseyNumber = 17
        };
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.AddRange(peer, membership);
            await db.SaveChangesAsync(Ct);
        }
        using var client = AuthenticatedTestClientFactory.Create(factory, s.Member);
        var url = $"/api/teams/{s.Pair.TeamB.Id}/members/{peer.Id}?currentUserId={s.Member.Id}";
        // Different ordinary Member target and no role field: only the generic
        // management boundary can deny these otherwise valid edit/delete requests.
        await Status(client.PutAsJsonAsync(url, new { badgeTitle = "Changed badge", teamJerseyNumber = 79 }, Ct), 403);
        await AssertPeerUnchanged();
        await Status(client.DeleteAsync(url, Ct), 403);
        await AssertPeerUnchanged();

        async Task AssertPeerUnchanged()
        {
            // A new scope after EACH request prevents tracked seed state masking a write.
            await using var scope = factory.Services.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var persisted = await db.TeamMemberships.AsNoTracking()
                .SingleAsync(x => x.TeamId == s.Pair.TeamB.Id && x.UserId == peer.Id, Ct);
            Assert.Equal(membership.Id, persisted.Id);
            Assert.Equal("Original badge", persisted.BadgeTitle);
            Assert.Equal(17, persisted.TeamJerseyNumber);
            Assert.Equal(TeamMemberRole.Member, persisted.Role);
        }
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
        // Admin cannot bypass the self-removal guard by spoofing the owner.
        await Status(client.DeleteAsync($"{root}/{s.Admin.Id}?currentUserId={s.Pair.UserB.Id}", Ct), 400);
        using var owner = AuthenticatedTestClientFactory.Create(factory, s.Pair.UserB);
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
    public async Task CreateJoinNumberAndLeave_UseJwt_AndIgnoreSuppliedUser(bool anonymous)
    {
        var s = await TeamApiBaselineScenarioBuilder.CreateAsync(factory.Services, publicB: true);
        var p = s.Pair;
        using var client = anonymous ? factory.CreateClient() : AuthenticatedTestClientFactory.Create(factory, p.UserA);
        var name = $"Created {Guid.NewGuid():N}";
        Guid? createdId = null;
        var create = client.PostAsJsonAsync($"/api/teams?currentUserId={p.UserB.Id}", new { name, visibility = 2 }, Ct);
        if (anonymous) await Status(create, 401);
        else
        {
            var created = await Json(create, 201);
            createdId = created["id"]!.GetValue<Guid>();
            Assert.Equal(p.UserA.Id, created["createdByUserId"]!.GetValue<Guid>());
            Assert.Equal(1, created["myRole"]!.GetValue<int>());
        }
        await using (var verify = factory.Services.CreateAsyncScope())
        {
            var db = verify.ServiceProvider.GetRequiredService<AppDbContext>();
            if (anonymous) Assert.False(await db.Teams.AnyAsync(x => x.Name == name, Ct));
            else
            {
                Assert.Equal(p.UserA.Id, (await db.Teams.SingleAsync(x => x.Id == createdId, Ct)).CreatedByUserId);
                Assert.Equal(p.UserA.Id, (await db.TeamMemberships.SingleAsync(x => x.TeamId == createdId, Ct)).UserId);
            }
        }
        // A is foreign to B: code joins JWT A, repeat remains idempotent, query B does nothing.
        for (var i = 0; i < 2; i++)
        {
            await Status(client.PostAsJsonAsync($"/api/teams/join-by-code?currentUserId={p.UserB.Id}", new { code = p.TeamB.InviteCode }, Ct), anonymous ? 401 : 200);
            await AssertMembership(null, !anonymous);
        }
        await Status(client.PutAsJsonAsync($"/api/teams/{p.TeamB.Id}/members/me/number?currentUserId={p.UserB.Id}", new { teamJerseyNumber = 79 }, Ct), anonymous ? 401 : 200);
        await AssertMembership(anonymous ? null : 79, !anonymous);
        await Status(client.DeleteAsync($"/api/teams/{p.TeamB.Id}/members/me?currentUserId={p.UserB.Id}", Ct), anonymous ? 401 : 204);
        await AssertMembership(null, false);
        await Status(client.PostAsync($"/api/teams/{p.TeamA.Id}/join-public?currentUserId={p.UserB.Id}", null, Ct), anonymous ? 401 : 400);
        await Status(client.PostAsync($"/api/teams/{p.TeamB.Id}/join-public?currentUserId={s.Member.Id}&teamJerseyNumber=0", null, Ct), anonymous ? 401 : 200);
        await AssertMembership(anonymous ? null : 0, !anonymous);

        async Task AssertMembership(int? number, bool exists)
        {
            await using var verify = factory.Services.CreateAsyncScope();
            var db = verify.ServiceProvider.GetRequiredService<AppDbContext>();
            var memberships = await db.TeamMemberships.AsNoTracking().Where(x => x.TeamId == p.TeamB.Id).ToListAsync(Ct);
            var actor = memberships.SingleOrDefault(x => x.UserId == p.UserA.Id);
            Assert.Equal(exists, actor != null);
            if (actor != null) { Assert.Equal(number, actor.TeamJerseyNumber); Assert.Equal(TeamMemberRole.Member, actor.Role); }
            Assert.Equal(3 + (exists ? 1 : 0), memberships.Count);
            Assert.Null(memberships.Single(x => x.UserId == p.UserB.Id).TeamJerseyNumber);
            Assert.Equal(TeamMemberRole.Owner, memberships.Single(x => x.UserId == p.UserB.Id).Role);
            Assert.Null(memberships.Single(x => x.UserId == s.Member.Id).TeamJerseyNumber);
            Assert.True(await db.TeamMemberships.AnyAsync(x => x.TeamId == p.TeamA.Id && x.UserId == p.UserA.Id, Ct));
        }
    }

    [Fact]
    public async Task Owner_RemovesTargetAdmin_WhenQueryMatchesTarget_NotJwtActor()
    {
        var s = await TeamApiBaselineScenarioBuilder.CreateAsync(factory.Services);
        using var client = AuthenticatedTestClientFactory.Create(factory, s.Pair.UserB);
        await Status(client.DeleteAsync($"/api/teams/{s.Pair.TeamB.Id}/members/{s.Admin.Id}?currentUserId={s.Admin.Id}", Ct), 204);
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.False(await db.TeamMemberships.AnyAsync(x => x.TeamId == s.Pair.TeamB.Id && x.UserId == s.Admin.Id, Ct));
        Assert.Equal(TeamMemberRole.Owner, (await db.TeamMemberships.SingleAsync(x => x.TeamId == s.Pair.TeamB.Id && x.UserId == s.Pair.UserB.Id, Ct)).Role);
        Assert.Equal(2, await db.TeamMemberships.CountAsync(x => x.TeamId == s.Pair.TeamB.Id, Ct));
    }

    [Fact]
    public async Task JoinPrivateByCode_JwtForeignJoinsItself_NotQueryOwner()
    {
        var s = await TeamApiBaselineScenarioBuilder.CreateAsync(factory.Services);
        using var client = AuthenticatedTestClientFactory.Create(factory, s.Pair.UserA);
        await Status(client.PostAsJsonAsync($"/api/teams/join-by-code?currentUserId={s.Pair.UserB.Id}",
            new { code = s.Pair.TeamB.InviteCode, teamJerseyNumber = 0 }, Ct), 200);
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var actor = await db.TeamMemberships.AsNoTracking().SingleAsync(x => x.TeamId == s.Pair.TeamB.Id && x.UserId == s.Pair.UserA.Id, Ct);
        Assert.Equal(TeamMemberRole.Member, actor.Role);
        Assert.Equal(0, actor.TeamJerseyNumber);
        Assert.Equal(4, await db.TeamMemberships.CountAsync(x => x.TeamId == s.Pair.TeamB.Id, Ct));
        var owner = await db.TeamMemberships.AsNoTracking().SingleAsync(x => x.TeamId == s.Pair.TeamB.Id && x.UserId == s.Pair.UserB.Id, Ct);
        Assert.Equal(TeamMemberRole.Owner, owner.Role);
        Assert.Null(owner.TeamJerseyNumber);
    }

    [Fact]
    public async Task OwnerLeave_WithOthersAndAloneIs400_AndOriginalOwnersRemain_TECH001()
    {
        var s = await TeamApiBaselineScenarioBuilder.CreateAsync(factory.Services);
        using var client = AuthenticatedTestClientFactory.Create(factory, s.Pair.UserB);
        await Status(client.DeleteAsync($"/api/teams/{s.Pair.TeamB.Id}/members/me?currentUserId={s.Pair.UserB.Id}", Ct), 400);
        using var ownerA = AuthenticatedTestClientFactory.Create(factory, s.Pair.UserA);
        await Status(ownerA.DeleteAsync($"/api/teams/{s.Pair.TeamA.Id}/members/me?currentUserId={s.Pair.UserA.Id}", Ct), 400);
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.True(await db.Teams.AnyAsync(x => x.Id == s.Pair.TeamA.Id, Ct));
        Assert.Equal(TeamMemberRole.Owner, (await db.TeamMemberships.SingleAsync(x => x.TeamId == s.Pair.TeamA.Id && x.UserId == s.Pair.UserA.Id, Ct)).Role);
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
