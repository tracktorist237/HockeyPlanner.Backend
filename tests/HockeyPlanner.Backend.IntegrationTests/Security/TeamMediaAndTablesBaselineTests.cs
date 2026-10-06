using System.Net.Http.Headers;
using System.Net.Http.Json;
using HockeyPlanner.Backend.Core.Entities;
using HockeyPlanner.Backend.Core.Enums;
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
    public async Task TeamsMedia_JwtRoleGuardsRejectSpoofedAndAnonymousOwners_SEC001Regression(string route, string field)
    {
        var s = await TeamApiBaselineScenarioBuilder.CreateAsync(factory.Services);
        var storage = new SequencedFileStorageService();
        using var host = factory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<IFileStorageService>();
            services.AddSingleton<IFileStorageService>(storage);
        }));
        foreach (var actor in new[] { "admin", "foreign", "owner", "member", "spoofed", "anonymous" })
        {
            using var client = host.CreateClient();
            if (actor != "anonymous")
            {
                using var jwtClient = AuthenticatedTestClientFactory.Create(factory, Actor(s, actor));
                client.DefaultRequestHeaders.Authorization = jwtClient.DefaultRequestHeaders.Authorization;
            }
            var userId = s.Pair.UserB.Id;
            var allowed = actor is "owner" or "admin";
            var callsBefore = storage.UploadCallCount;
            string? avatarBefore;
            string? coverBefore;
            await using (var beforeScope = factory.Services.CreateAsyncScope())
            {
                var beforeDb = beforeScope.ServiceProvider.GetRequiredService<AppDbContext>();
                var before = await beforeDb.Teams.AsNoTracking().SingleAsync(x => x.Id == s.Pair.TeamB.Id, Ct);
                avatarBefore = before.AvatarUrl;
                coverBefore = before.CoverImageUrl;
            }
            using var content = new MultipartFormDataContent();
            var png = new ByteArrayContent(Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+jRZkAAAAASUVORK5CYII="));
            png.Headers.ContentType = new MediaTypeHeaderValue("image/png");
            content.Add(png, "file", "pixel.png");
            var pending = client.PostAsync($"/api/teams/{s.Pair.TeamB.Id}/{route}?currentUserId={userId}", content, Ct);
            string? returnedUrl = null;
            if (allowed)
            {
                returnedUrl = (await Json(pending))[field]!.GetValue<string>();
                Assert.Equal(storage.LastUploadedUrl, returnedUrl);
                Assert.Equal($"https://test.invalid/teams/upload-{callsBefore + 1}.png", returnedUrl);
                if (field == "avatarUrl") Assert.NotEqual(avatarBefore, returnedUrl);
                if (field == "coverImageUrl") Assert.NotEqual(coverBefore, returnedUrl);
            }
            else await Status(pending, actor == "anonymous" ? 401 : 403);
            Assert.Equal(callsBefore + (allowed ? 1 : 0), storage.UploadCallCount);
            await using var scope = factory.Services.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var team = await db.Teams.SingleAsync(x => x.Id == s.Pair.TeamB.Id, Ct);
            Assert.Equal(allowed && field == "avatarUrl" ? returnedUrl : avatarBefore, team.AvatarUrl);
            Assert.Equal(allowed && field == "coverImageUrl" ? returnedUrl : coverBefore, team.CoverImageUrl);
            var foreign = await db.Teams.SingleAsync(x => x.Id == s.Pair.TeamA.Id, Ct);
            Assert.Null(foreign.AvatarUrl);
            Assert.Null(foreign.CoverImageUrl);
        }
    }

    // HP-83: JWT identity replaces the original HP-79 insecure characterizations.
    [Theory]
    [InlineData("owner", false, 200, 200)]
    [InlineData("admin", false, 200, 200)]
    [InlineData("member", false, 200, 403)]
    [InlineData("foreign", false, 403, 403)]
    [InlineData("anonymous", false, 401, 401)]
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

    [Fact]
    public async Task DuplicateProtocol409_DoesNotSyncMissingTableRows_HP83()
    {
        var s = await TeamApiBaselineScenarioBuilder.CreateAsync(factory.Services);
        var lateMember = new User { FirstName = "Late", LastName = "Member", EmailConfirmed = true };
        EventTableProtocol before;
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.AddRange(lateMember, new TeamMembership
            {
                TeamId = s.Pair.TeamB.Id, UserId = lateMember.Id, Role = TeamMemberRole.Member
            });
            await db.SaveChangesAsync(Ct);
            Assert.False(await db.TeamTableRows.AnyAsync(x => x.TeamTableId == s.TableB.Id && x.UserId == lateMember.Id, Ct));
            Assert.Equal(3, await db.TeamTableRows.CountAsync(x => x.TeamTableId == s.TableB.Id, Ct));
            Assert.Equal(1, await db.EventTableProtocols.CountAsync(x => x.EventId == s.Pair.EventB.Id, Ct));
            before = await db.EventTableProtocols.AsNoTracking().Include(x => x.Rows)
                .SingleAsync(x => x.Id == s.ProtocolB.Id, Ct);
        }
        using var client = AuthenticatedTestClientFactory.Create(factory, s.Pair.UserB);
        await Status(client.PostAsJsonAsync(
            $"/api/events/{s.Pair.EventB.Id}/table-protocols?currentUserId={s.Pair.UserB.Id}",
            new { teamTableId = s.TableB.Id }, Ct), 409);

        await using var verify = factory.Services.CreateAsyncScope();
        var context = verify.ServiceProvider.GetRequiredService<AppDbContext>();
        // A rejected duplicate must leave the missing row missing.
        Assert.Empty(await context.TeamTableRows.AsNoTracking()
            .Where(x => x.TeamTableId == s.TableB.Id && x.UserId == lateMember.Id).ToListAsync(Ct));
        Assert.Equal(3, await context.TeamTableRows.CountAsync(x => x.TeamTableId == s.TableB.Id, Ct));
        var after = Assert.Single(await context.EventTableProtocols.AsNoTracking().Include(x => x.Rows)
            .Where(x => x.EventId == s.Pair.EventB.Id).ToListAsync(Ct));
        Assert.Equal(before.Id, after.Id);
        Assert.Equal(before.EventId, after.EventId);
        Assert.Equal(before.TeamTableId, after.TeamTableId);
        Assert.Equal(before.CreatedByUserId, after.CreatedByUserId);
        Assert.Equal(before.CreatedAt, after.CreatedAt);
        Assert.Equal(before.UpdatedAt, after.UpdatedAt);
        Assert.Equal(
            before.Rows.OrderBy(x => x.Id).Select(x => (x.Id, x.UserId, x.Games, x.Goals, x.Assists, x.Points, x.CreatedAt, x.UpdatedAt)).ToArray(),
            after.Rows.OrderBy(x => x.Id).Select(x => (x.Id, x.UserId, x.Games, x.Goals, x.Assists, x.Points, x.CreatedAt, x.UpdatedAt)).ToArray());
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
    public async Task ForeignTableGuid_DoesNotInsertOwnMembersBefore404_HP83()
    {
        var s = await TeamApiBaselineScenarioBuilder.CreateAsync(factory.Services);
        using var client = AuthenticatedTestClientFactory.Create(factory, s.Pair.UserA);
        TeamTableRow[] before;
        await using (var beforeScope = factory.Services.CreateAsyncScope())
        {
            var beforeDb = beforeScope.ServiceProvider.GetRequiredService<AppDbContext>();
            before = await beforeDb.TeamTableRows.AsNoTracking().Where(x => x.TeamTableId == s.TableB.Id).OrderBy(x => x.Id).ToArrayAsync(Ct);
            Assert.Equal(3, before.Length);
            Assert.DoesNotContain(before, x => x.UserId == s.Pair.UserA.Id);
        }
        await Status(client.GetAsync($"/api/teams/{s.Pair.TeamA.Id}/tables/{s.TableB.Id}?currentUserId={s.Pair.UserA.Id}", Ct), 404);
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        // Verify every persisted row field in a fresh scope.
        var after = await db.TeamTableRows.AsNoTracking().Where(x => x.TeamTableId == s.TableB.Id).OrderBy(x => x.Id).ToArrayAsync(Ct);
        Assert.Equal(before.Select(x => (x.Id, x.UserId, x.Games, x.Goals, x.Assists, x.Points, x.CreatedAt, x.UpdatedAt)),
            after.Select(x => (x.Id, x.UserId, x.Games, x.Goals, x.Assists, x.Points, x.CreatedAt, x.UpdatedAt)));
        Assert.False(await db.TeamTableRows.AnyAsync(x => x.TeamTableId == s.TableB.Id && x.UserId == s.Pair.UserA.Id, Ct));
        Assert.Equal(3, await db.TeamTableRows.CountAsync(x => x.TeamTableId == s.TableB.Id, Ct));
        Assert.Equal(s.Pair.TeamB.Id, (await db.TeamTables.SingleAsync(x => x.Id == s.TableB.Id, Ct)).TeamId);
    }

    private sealed class SequencedFileStorageService : IFileStorageService
    {
        public int UploadCallCount { get; private set; }
        public string? LastUploadedUrl { get; private set; }

        public Task<FileStorageUploadResult> UploadAsync(FileStorageUploadRequest request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            LastUploadedUrl = $"https://test.invalid/teams/upload-{++UploadCallCount}.png";
            return Task.FromResult(new FileStorageUploadResult
            {
                PublicUrl = LastUploadedUrl, Key = $"test/upload-{UploadCallCount}.png"
            });
        }

        public Task DeleteAsync(string key, CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
