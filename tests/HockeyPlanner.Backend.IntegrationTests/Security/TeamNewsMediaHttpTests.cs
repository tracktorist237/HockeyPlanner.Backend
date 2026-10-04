using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using HockeyPlanner.Backend.Core.Entities;
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
[Trait("Category", "HP82")]
public sealed class TeamNewsMediaHttpTests(HockeyPlannerWebApplicationFactory factory)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task NewsLists_AreNewestFirstBoundedAndExcludeNonMemberPublicTeamsFromFeed()
    {
        var s = await TeamApiBaselineScenarioBuilder.CreateAsync(factory.Services, publicB: true);
        var instant = new DateTime(2040, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var rows = Enumerable.Range(0, 105).Select(i => new TeamNews
        {
            TeamId = s.Pair.TeamB.Id, AuthorUserId = s.Admin.Id, Title = $"Bounded {i}", Body = "Body",
            CreatedAt = instant.AddMinutes(i), UpdatedAt = instant.AddMinutes(i)
        }).ToArray();
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.TeamNews.AddRange(rows);
            await db.SaveChangesAsync(Ct);
        }
        using var client = Client(factory, s, "member");
        var list = (await Json(client.GetAsync($"/api/teams/{s.Pair.TeamB.Id}/news", Ct))).AsArray();
        var feed = (await Json(client.GetAsync("/api/news", Ct))).AsArray();
        Assert.Equal(rows.Reverse().Take(50).Select(x => x.Id), list.Select(x => x!["id"]!.GetValue<Guid>()));
        Assert.Equal(rows.Reverse().Take(100).Select(x => x.Id), feed.Select(x => x!["id"]!.GetValue<Guid>()));
        using var foreign = Client(factory, s, "foreign");
        Assert.DoesNotContain((await Json(foreign.GetAsync("/api/news", Ct))).AsArray(), x => x!["teamId"]!.GetValue<Guid>() == s.Pair.TeamB.Id);
    }

    [Fact]
    public async Task NewsCreateAndUpdate_PreserveStringBoundsAndMissingFeedUser404()
    {
        var s = await TeamApiBaselineScenarioBuilder.CreateAsync(factory.Services);
        using var owner = Client(factory, s, "owner");
        var payload = new { title = new string('t', 130), body = new string('b', 2010), imageUrl = "https://test.invalid/" + new string('i', 510) };
        var root = $"/api/teams/{s.Pair.TeamB.Id}/news";
        var created = await Json(owner.PostAsJsonAsync(root, payload, Ct));
        var updated = await Json(owner.PutAsJsonAsync($"{root}/{s.NewsB.Id}", payload, Ct));
        foreach (var dto in new[] { created, updated })
        {
            Assert.Equal(120, dto["title"]!.GetValue<string>().Length);
            Assert.Equal(2000, dto["body"]!.GetValue<string>().Length);
            Assert.Equal(500, dto["imageUrl"]!.GetValue<string>().Length);
        }
        using var missing = factory.CreateClient();
        using var scope = factory.Services.CreateScope();
        missing.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", scope.ServiceProvider.GetRequiredService<IAuthTokenService>().CreateAccessToken(new User { FirstName = "Missing" }));
        var problem = await Json(missing.GetAsync("/api/news", Ct), 404);
        Assert.Equal("Пользователь не найден.", problem["detail"]!.GetValue<string>());
    }

    [Theory]
    [InlineData("anonymous", false, 401)]
    [InlineData("foreign", false, 403)]
    [InlineData("member", false, 200)]
    [InlineData("admin", false, 200)]
    [InlineData("owner", false, 200)]
    [InlineData("anonymous", true, 200)]
    [InlineData("foreign", true, 200)]
    [InlineData("member", true, 200)]
    [InlineData("admin", true, 200)]
    [InlineData("owner", true, 200)]
    public async Task NewsReads_PreserveVisibilityAndMembershipFeed(string actor, bool publicTeam, int expected)
    {
        var s = await TeamApiBaselineScenarioBuilder.CreateAsync(factory.Services, publicTeam);
        using var client = Client(factory, s, actor);
        var query = $"?currentUserId={s.Pair.UserB.Id}";
        var response = client.GetAsync($"/api/teams/{s.Pair.TeamB.Id}/news{query}", Ct);
        if (expected != 200) await Status(response, expected);
        else
        {
            var item = Assert.Single((await Json(response)).AsArray())!;
            Assert.Equal(s.NewsB.Id, item["id"]!.GetValue<Guid>());
            Assert.Equal(s.Pair.TeamB.Name, item["teamName"]!.GetValue<string>());
            Assert.Equal(actor is "admin" or "owner", item["canManage"]!.GetValue<bool>());
        }
        await Status(client.GetAsync($"/api/teams/{Guid.NewGuid()}/news{query}", Ct), 404);
        if (actor == "anonymous") await Status(client.GetAsync("/api/news" + query, Ct), 401);
        else
        {
            var feed = (await Json(client.GetAsync("/api/news" + query, Ct))).AsArray();
            Assert.Contains(feed, x => x!["id"]!.GetValue<Guid>() == (actor == "foreign" ? s.NewsA.Id : s.NewsB.Id));
            Assert.DoesNotContain(feed, x => x!["id"]!.GetValue<Guid>() == (actor == "foreign" ? s.NewsB.Id : s.NewsA.Id));
            var item = Assert.Single(feed, x => x!["id"]!.GetValue<Guid>() == (actor == "foreign" ? s.NewsA.Id : s.NewsB.Id))!;
            Assert.Equal(actor != "member", item["canManage"]!.GetValue<bool>());
        }
    }

    [Theory]
    [InlineData("anonymous", 401)]
    [InlineData("foreign", 403)]
    [InlineData("member", 403)]
    [InlineData("admin", 200)]
    [InlineData("owner", 200)]
    public async Task NewsMutations_PreserveJwtMatrixNormalizationAndNotFoundPrecedence(string actor, int expected)
    {
        var s = await TeamApiBaselineScenarioBuilder.CreateAsync(factory.Services);
        using var client = Client(factory, s, actor);
        var root = $"/api/teams/{s.Pair.TeamB.Id}/news";
        var query = $"?currentUserId={s.Pair.UserB.Id}";
        var title = " HP82   title ";
        var body = " HP82 body ";
        var image = " https://test.invalid/news.png ";
        var payload = new { title, body, imageUrl = image, sendNotification = false, authorUserId = s.Pair.UserB.Id, currentUserId = s.Pair.UserB.Id };
        var creation = client.PostAsJsonAsync(root + query, payload, Ct);
        Guid? createdId = null;
        if (expected == 200)
        {
            var dto = await Json(creation);
            createdId = dto["id"]!.GetValue<Guid>();
            Assert.Equal(Actor(s, actor).Id, dto["authorUserId"]!.GetValue<Guid>());
            Assert.Equal("", dto["teamName"]!.GetValue<string>());
            Assert.Equal("HP82 title", dto["title"]!.GetValue<string>());
            Assert.Equal("HP82 body", dto["body"]!.GetValue<string>());
            Assert.Equal(image.Trim(), dto["imageUrl"]!.GetValue<string>());
            Assert.True(dto["canManage"]!.GetValue<bool>());
        }
        else await Status(creation, expected);
        var update = client.PutAsJsonAsync($"{root}/{s.NewsB.Id}{query}", payload, Ct);
        if (expected == 200)
        {
            var dto = await Json(update);
            Assert.Equal(s.Pair.TeamB.Name, dto["teamName"]!.GetValue<string>());
            Assert.Equal(s.Pair.UserB.Id, dto["authorUserId"]!.GetValue<Guid>());
            Assert.Equal("HP82 title", dto["title"]!.GetValue<string>());
        }
        else await Status(update, expected);
        foreach (var target in new[] { Guid.NewGuid(), s.NewsA.Id })
        {
            await Status(client.PutAsJsonAsync($"{root}/{target}{query}", payload, Ct), expected == 200 ? 404 : expected);
            await Status(client.DeleteAsync($"{root}/{target}{query}", Ct), expected == 200 ? 404 : expected);
        }
        await Status(client.DeleteAsync($"{root}/{s.NewsB.Id}{query}", Ct), expected == 200 ? 204 : expected);
        // Create validates before role lookup; update checks role before validation.
        await Status(client.PostAsJsonAsync(root + query, new { title = " ", body = " " }, Ct), actor == "anonymous" ? 401 : 400);
        await Status(client.PutAsJsonAsync($"{root}/{Guid.NewGuid()}{query}", new { title = " ", body = " " }, Ct), expected == 200 ? 400 : expected);
        await Status(client.PostAsJsonAsync($"/api/teams/{Guid.NewGuid()}/news{query}", payload, Ct), actor == "anonymous" ? 401 : 403);
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.Equal(expected != 200, await db.TeamNews.AnyAsync(x => x.Id == s.NewsB.Id, Ct));
        Assert.Equal(expected == 200 ? 1 : 0, await db.TeamNews.CountAsync(x => x.TeamId == s.Pair.TeamB.Id && x.Title == "HP82 title", Ct));
        if (createdId.HasValue) Assert.Equal(Actor(s, actor).Id, (await db.TeamNews.SingleAsync(x => x.Id == createdId, Ct)).AuthorUserId);
        Assert.Equal("News A", (await db.TeamNews.SingleAsync(x => x.Id == s.NewsA.Id, Ct)).Title);
    }

    public static IEnumerable<object[]> MediaActors() =>
        from route in new[] { "avatar/upload", "cover/upload", "news/upload-image" }
        from actor in new[] { "anonymous", "foreign", "member", "admin", "owner" }
        select new object[] { route, actor };

    [Theory]
    [MemberData(nameof(MediaActors))]
    public async Task Media_PreservesAuthorizationStorageScopeAndDto(string route, string actor)
    {
        var s = await TeamApiBaselineScenarioBuilder.CreateAsync(factory.Services);
        var storage = new RecordingStorage();
        using var host = StorageHost(storage);
        using var client = Client(host, s, actor);
        using var content = Image();
        var expected = actor == "anonymous" ? 401 : actor is "owner" or "admin" ? 200 : 403;
        var pending = client.PostAsync($"/api/teams/{s.Pair.TeamB.Id}/{route}?currentUserId={s.Pair.UserB.Id}&key=arbitrary", content, Ct);
        if (expected == 200)
        {
            var dto = await Json(pending);
            var field = route.StartsWith("avatar") ? "avatarUrl" : route.StartsWith("cover") ? "coverImageUrl" : "imageUrl";
            Assert.Equal(RecordingStorage.Url, dto[field]!.GetValue<string>());
            if (field == "imageUrl") Assert.Single(dto.AsObject());
            else
            {
                Assert.Equal(s.Pair.TeamB.Id, dto["id"]!.GetValue<Guid>());
                Assert.Equal(s.Pair.TeamB.InviteCode, dto["inviteCode"]!.GetValue<string>());
                Assert.Equal(actor == "owner" ? 1 : 2, dto["myRole"]!.GetValue<int>());
                Assert.Equal(3, dto["membersCount"]!.GetValue<int>());
                Assert.Null(dto["myTeamJerseyNumber"]);
            }
            Assert.Equal(route.StartsWith("news") ? FileStorageFolders.News : FileStorageFolders.Teams, storage.Last!.Folder);
            Assert.Equal(s.Pair.TeamB.Id.ToString("N"), storage.Last.ScopeId);
            Assert.Equal("pixel.png", storage.Last.FileName);
            Assert.Equal("image/png", storage.Last.ContentType);
            Assert.True(storage.Token.CanBeCanceled);
        }
        else await Status(pending, expected);
        Assert.Equal(expected == 200 ? 1 : 0, storage.Calls);
        using var missing = Image();
        await Status(client.PostAsync($"/api/teams/{Guid.NewGuid()}/{route}", missing, Ct), actor == "anonymous" ? 401 : route.StartsWith("news") ? 403 : 404);
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var team = await db.Teams.AsNoTracking().SingleAsync(x => x.Id == s.Pair.TeamB.Id, Ct);
        Assert.Equal(expected == 200 && route.StartsWith("avatar") ? RecordingStorage.Url : null, team.AvatarUrl);
        Assert.Equal(expected == 200 && route.StartsWith("cover") ? RecordingStorage.Url : null, team.CoverImageUrl);
    }

    [Theory]
    [InlineData("avatar/upload")]
    [InlineData("cover/upload")]
    [InlineData("news/upload-image")]
    public async Task Media_InvalidFilesNeverCallStorage_AndErrorsStaySafe(string route)
    {
        var s = await TeamApiBaselineScenarioBuilder.CreateAsync(factory.Services);
        var storage = new RecordingStorage();
        using var host = StorageHost(storage);
        using var client = Client(host, s, "owner");
        foreach (var invalid in new[] { ("pixel.png", "image/png", 0), ("pixel.png", "text/plain", 1), ("pixel.svg", "image/svg+xml", 1), ("pixel.png", "image/png", 5 * 1024 * 1024 + 1) })
        {
            using var content = Image(invalid.Item1, invalid.Item2, invalid.Item3);
            await Status(client.PostAsync($"/api/teams/{s.Pair.TeamB.Id}/{route}", content, Ct), 400);
        }
        Assert.Equal(0, storage.Calls);
        storage.Failure = new HockeyPlanner.Backend.Core.Exceptions.BusinessRuleException("Safe storage validation");
        using (var content = Image())
        {
            var problem = await Json(client.PostAsync($"/api/teams/{s.Pair.TeamB.Id}/{route}", content, Ct), 400);
            Assert.Equal("Safe storage validation", problem["detail"]!.GetValue<string>());
        }
        storage.Failure = new IOException("private provider secret");
        using (var content = Image())
        {
            var problem = await Json(client.PostAsync($"/api/teams/{s.Pair.TeamB.Id}/{route}", content, Ct), 502);
            Assert.DoesNotContain("private provider secret", problem.ToJsonString());
        }
        await using var scope = factory.Services.CreateAsyncScope();
        var team = await scope.ServiceProvider.GetRequiredService<AppDbContext>().Teams.AsNoTracking().SingleAsync(x => x.Id == s.Pair.TeamB.Id, Ct);
        Assert.Null(team.AvatarUrl);
        Assert.Null(team.CoverImageUrl);
    }

    [Theory]
    [InlineData("avatar/upload")]
    [InlineData("cover/upload")]
    [InlineData("news/upload-image")]
    public async Task Media_PreservesAllAllowedExtensionsBroadImageMimeAndExactMaximum(string route)
    {
        var s = await TeamApiBaselineScenarioBuilder.CreateAsync(factory.Services);
        var storage = new RecordingStorage();
        using var host = StorageHost(storage);
        using var client = Client(host, s, "admin");
        foreach (var extension in new[] { ".JPG", ".jpeg", ".png", ".webp", ".gif" })
        {
            // Existing validation accepts any image/* MIME with these extensions.
            using var content = Image("pixel" + extension, "image/custom", extension == ".gif" ? 5 * 1024 * 1024 : 1);
            await Status(client.PostAsync($"/api/teams/{s.Pair.TeamB.Id}/{route}", content, Ct), 200);
        }
        Assert.Equal(5, storage.Calls);
    }

    private Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<HockeyPlanner.Backend.WebAPI.Program> StorageHost(RecordingStorage storage) =>
        factory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<IFileStorageService>();
            services.AddSingleton<IFileStorageService>(storage);
        }));

    private static HttpClient Client(Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<HockeyPlanner.Backend.WebAPI.Program> host, TeamApiBaselineScenario s, string actor)
    {
        var client = host.CreateClient();
        if (actor != "anonymous")
        {
            using var scope = host.Services.CreateScope();
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", scope.ServiceProvider.GetRequiredService<IAuthTokenService>().CreateAccessToken(Actor(s, actor)));
        }
        return client;
    }

    internal static MultipartFormDataContent Image(string name = "pixel.png", string type = "image/png", int? length = null)
    {
        var content = new MultipartFormDataContent();
        var file = new ByteArrayContent(length.HasValue ? new byte[length.Value] : Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+jRZkAAAAASUVORK5CYII="));
        file.Headers.ContentType = new MediaTypeHeaderValue(type);
        content.Add(file, "file", name);
        return content;
    }

    private sealed class RecordingStorage : IFileStorageService
    {
        public const string Url = "https://test.invalid/existing-response.png";
        public int Calls;
        public FileStorageUploadRequest? Last;
        public CancellationToken Token;
        public Exception? Failure;
        public Task<FileStorageUploadResult> UploadAsync(FileStorageUploadRequest request, CancellationToken cancellationToken)
        {
            Calls++;
            Last = request;
            Token = cancellationToken;
            return Failure is null ? Task.FromResult(new FileStorageUploadResult { PublicUrl = Url, Key = "test/key" }) : Task.FromException<FileStorageUploadResult>(Failure);
        }
        public Task DeleteAsync(string key, CancellationToken cancellationToken) => throw new InvalidOperationException("HP82 must not delete storage objects");
    }
}
