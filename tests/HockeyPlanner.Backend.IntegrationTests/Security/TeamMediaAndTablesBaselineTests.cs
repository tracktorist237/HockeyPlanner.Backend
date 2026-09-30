using System.Net.Http.Headers;
using System.Net.Http.Json;
using HockeyPlanner.Backend.Infrastructure.Data;
using HockeyPlanner.Backend.IntegrationTests.Fixtures;
using HockeyPlanner.Backend.IntegrationTests.Infrastructure;
using HockeyPlanner.Backend.WebAPI.Services;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using static HockeyPlanner.Backend.IntegrationTests.Security.TeamApiBaselineTests;

namespace HockeyPlanner.Backend.IntegrationTests.Security;

[Collection(IntegrationTestCollection.Name)]
[Trait("Category", "HP79")]
public sealed class TeamMediaAndTablesBaselineTests(HockeyPlannerWebApplicationFactory factory)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData("avatar/upload", "avatarUrl")]
    [InlineData("cover/upload", "coverImageUrl")]
    [InlineData("news/upload-image", "imageUrl")]
    public async Task Uploads_RoleGuardsAndSpoofedOrAnonymousOwnerSuccess_Document_SEC001(string route, string field)
    {
        var s = await TeamApiBaselineScenarioBuilder.CreateAsync(factory.Services);
        var storage = new SpyFileStorageService();
        using var host = factory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<IFileStorageService>();
            services.AddSingleton<IFileStorageService>(storage);
        }));
        foreach (var actor in new[] { "foreign", "member", "admin", "owner", "spoofed", "anonymous" })
        {
            using var client = host.CreateClient();
            if (actor != "anonymous")
            {
                using var jwtClient = AuthenticatedTestClientFactory.Create(factory, Actor(s, actor));
                client.DefaultRequestHeaders.Authorization = jwtClient.DefaultRequestHeaders.Authorization;
            }
            var userId = actor is "spoofed" or "anonymous" ? s.Pair.UserB.Id : Actor(s, actor).Id;
            var allowed = actor is not ("foreign" or "member");
            var callsBefore = storage.UploadCallCount;
            using var content = new MultipartFormDataContent();
            var png = new ByteArrayContent(Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+jRZkAAAAASUVORK5CYII="));
            png.Headers.ContentType = new MediaTypeHeaderValue("image/png");
            content.Add(png, "file", "pixel.png");
            var pending = client.PostAsync($"/api/teams/{s.Pair.TeamB.Id}/{route}?currentUserId={userId}", content, Ct);
            if (allowed) Assert.Equal(storage.PublicUrl, (await Json(pending))[field]!.GetValue<string>());
            else await Status(pending, 403);
            Assert.Equal(callsBefore + (allowed ? 1 : 0), storage.UploadCallCount);
            await using var scope = factory.Services.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var team = await db.Teams.SingleAsync(x => x.Id == s.Pair.TeamB.Id, Ct);
            if (field != "imageUrl") Assert.Equal(allowed ? storage.PublicUrl : null, field == "avatarUrl" ? team.AvatarUrl : team.CoverImageUrl);
            var foreign = await db.Teams.SingleAsync(x => x.Id == s.Pair.TeamA.Id, Ct);
            Assert.Null(foreign.AvatarUrl);
            Assert.Null(foreign.CoverImageUrl);
        }
    }

    [Theory]
    [InlineData("owner", false, 200, 200)]
    [InlineData("admin", false, 200, 200)]
    [InlineData("member", false, 200, 403)]
    [InlineData("foreign", false, 403, 403)]
    [InlineData("anonymous", false, 403, 403)]
    [InlineData("foreign", true, 403, 403)]
    public async Task HonestQuery_TableAndProtocolReadManageMatrix(string actor, bool publicTeam, int readStatus, int manageStatus)
    {
        var s = await TeamApiBaselineScenarioBuilder.CreateAsync(factory.Services, publicTeam);
        var user = Actor(s, actor);
        using var client = actor == "anonymous" ? factory.CreateClient() : AuthenticatedTestClientFactory.Create(factory, user);
        var query = actor == "anonymous" ? "" : $"?currentUserId={user.Id}";
        var root = $"/api/teams/{s.Pair.TeamB.Id}/tables";
        await Status(client.GetAsync(root + query, Ct), readStatus);
        await Status(client.GetAsync($"{root}/{s.TableB.Id}{query}", Ct), readStatus);
        await Status(client.GetAsync($"/api/events/{s.Pair.EventB.Id}/table-protocols{query}", Ct), readStatus);
        await Status(client.PostAsJsonAsync(root + query, new { name = "Created table", templateType = 1 }, Ct), manageStatus);
        await Status(client.PostAsJsonAsync($"/api/events/{s.Pair.EventB.Id}/table-protocols{query}", new { teamTableId = s.TableB.Id }, Ct), manageStatus == 200 ? 409 : manageStatus);
        await Status(client.PutAsJsonAsync($"/api/events/{s.Pair.EventB.Id}/table-protocols/{s.ProtocolB.Id}{query}", new { rows = Array.Empty<object>() }, Ct), manageStatus);
        var row = Assert.Single(s.ProtocolB.Rows);
        await Status(client.PutAsJsonAsync($"/api/events/{s.Pair.EventB.Id}/table-protocols/{s.ProtocolB.Id}/rows/{row.Id}{query}", new { games = 1, goals = 2, assists = 3 }, Ct), manageStatus);
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.Equal(manageStatus == 200 ? 1 : 0, await db.TeamTables.CountAsync(x => x.TeamId == s.Pair.TeamB.Id && x.Name == "Created table", Ct));
        Assert.Equal(manageStatus == 200 ? 5 : 0, (await db.EventTableProtocolRows.SingleAsync(x => x.Id == row.Id, Ct)).Points);
        Assert.Equal(0, (await db.EventTableProtocolRows.SingleAsync(x => x.EventTableProtocolId == s.ProtocolA.Id, Ct)).Points);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SpoofedQuery_TableAndProtocolAccessCurrentlyActsAsOwner_Documents_SEC001(bool anonymous)
    {
        var s = await TeamApiBaselineScenarioBuilder.CreateAsync(factory.Services);
        using var client = anonymous ? factory.CreateClient() : AuthenticatedTestClientFactory.Create(factory, s.Pair.UserA);
        var query = $"?currentUserId={s.Pair.UserB.Id}";
        var tables = await Json(client.GetAsync($"/api/teams/{s.Pair.TeamB.Id}/tables{query}", Ct));
        Assert.True(Assert.Single(tables.AsArray())!["canManage"]!.GetValue<bool>());
        var feed = await Json(client.GetAsync($"/api/news/tables{query}", Ct));
        Assert.Equal(s.TableB.Id, Assert.Single(feed.AsArray())!["id"]!.GetValue<Guid>());
        var table = await Json(client.GetAsync($"/api/teams/{s.Pair.TeamB.Id}/tables/{s.TableB.Id}{query}", Ct));
        Assert.Equal(3, table["rows"]!.AsArray().Count);
        var protocols = await Json(client.GetAsync($"/api/events/{s.Pair.EventB.Id}/table-protocols{query}", Ct));
        Assert.Equal(s.ProtocolB.Id, Assert.Single(protocols.AsArray())!["id"]!.GetValue<Guid>());
        var row = Assert.Single(s.ProtocolB.Rows);
        await Status(client.PutAsJsonAsync($"/api/events/{s.Pair.EventB.Id}/table-protocols/{s.ProtocolB.Id}{query}", new { rows = new[] { new { rowId = row.Id, games = 1, goals = 7, assists = 2 } } }, Ct), 200);
        await Status(client.PostAsJsonAsync($"/api/teams/{s.Pair.TeamB.Id}/tables{query}", new { name = "Spoofed", templateType = 1 }, Ct), 200);
        // Clear only this scenario's protocol to exercise real creation and duplicate 409.
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            Assert.Equal(9, (await db.EventTableProtocolRows.SingleAsync(x => x.Id == row.Id, Ct)).Points);
            Assert.Equal(9, (await db.TeamTableRows.SingleAsync(x => x.TeamTableId == s.TableB.Id && x.UserId == s.Pair.UserB.Id, Ct)).Points);
            Assert.Equal(s.Pair.UserB.Id, (await db.TeamTables.SingleAsync(x => x.TeamId == s.Pair.TeamB.Id && x.Name == "Spoofed", Ct)).CreatedByUserId);
            db.EventTableProtocols.Remove(await db.EventTableProtocols.SingleAsync(x => x.Id == s.ProtocolB.Id, Ct));
            await db.SaveChangesAsync(Ct);
        }
        var created = await Json(client.PostAsJsonAsync($"/api/events/{s.Pair.EventB.Id}/table-protocols{query}", new { teamTableId = s.TableB.Id }, Ct));
        Assert.Equal(s.Pair.EventB.Id, created["eventId"]!.GetValue<Guid>());
        await Status(client.PostAsJsonAsync($"/api/events/{s.Pair.EventB.Id}/table-protocols{query}", new { teamTableId = s.TableB.Id }, Ct), 409);
        await using var verify = factory.Services.CreateAsyncScope();
        var context = verify.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.Equal(s.Pair.UserB.Id, (await context.EventTableProtocols.SingleAsync(x => x.EventId == s.Pair.EventB.Id, Ct)).CreatedByUserId);
    }

    [Fact]
    public async Task ForeignEventProtocolTableAndRowSubstitutions_Return404_WithoutChangingProtocols()
    {
        var s = await TeamApiBaselineScenarioBuilder.CreateAsync(factory.Services);
        using var client = AuthenticatedTestClientFactory.Create(factory, s.Pair.UserA);
        var query = $"?currentUserId={s.Pair.UserA.Id}";
        var root = $"/api/events/{s.EventA.Id}/table-protocols";
        await Status(client.PostAsJsonAsync(root + query, new { teamTableId = s.TableB.Id }, Ct), 404);
        await Status(client.PutAsJsonAsync($"{root}/{s.ProtocolB.Id}{query}", new { rows = Array.Empty<object>() }, Ct), 404);
        await Status(client.PutAsJsonAsync($"{root}/{s.ProtocolA.Id}/rows/{Assert.Single(s.ProtocolB.Rows).Id}{query}", new { games = 1, goals = 10, assists = 0 }, Ct), 404);
        await Status(client.GetAsync($"/api/events/{Guid.NewGuid()}/table-protocols{query}", Ct), 404);
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.Equal(2, await db.EventTableProtocols.CountAsync(x => x.Id == s.ProtocolA.Id || x.Id == s.ProtocolB.Id, Ct));
        Assert.All(await db.EventTableProtocolRows.Where(x => x.EventTableProtocolId == s.ProtocolA.Id || x.EventTableProtocolId == s.ProtocolB.Id).ToListAsync(Ct), x => Assert.Equal(0, x.Points));
    }

    [Fact]
    public async Task ForeignTableGuid_CurrentlyInsertsOwnMembersBefore404_Documents_SEC001_ARC002()
    {
        var s = await TeamApiBaselineScenarioBuilder.CreateAsync(factory.Services);
        using var client = AuthenticatedTestClientFactory.Create(factory, s.Pair.UserA);
        await Status(client.GetAsync($"/api/teams/{s.Pair.TeamA.Id}/tables/{s.TableB.Id}?currentUserId={s.Pair.UserA.Id}", Ct), 404);
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        // HP-83 must flip this assertion: a rejected read currently writes into the foreign table.
        Assert.True(await db.TeamTableRows.AnyAsync(x => x.TeamTableId == s.TableB.Id && x.UserId == s.Pair.UserA.Id, Ct));
        Assert.Equal(4, await db.TeamTableRows.CountAsync(x => x.TeamTableId == s.TableB.Id, Ct));
        Assert.Equal(s.Pair.TeamB.Id, (await db.TeamTables.SingleAsync(x => x.Id == s.TableB.Id, Ct)).TeamId);
    }
}
