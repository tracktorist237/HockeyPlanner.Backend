using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using HockeyPlanner.Backend.Core.Entities;
using HockeyPlanner.Backend.Core.Enums;
using HockeyPlanner.Backend.Infrastructure.Data;
using HockeyPlanner.Backend.IntegrationTests.Fixtures;
using HockeyPlanner.Backend.IntegrationTests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using static HockeyPlanner.Backend.IntegrationTests.Security.TeamApiBaselineTests;

namespace HockeyPlanner.Backend.IntegrationTests.Security;

[Collection(IntegrationTestCollection.Name)]
[Trait("Category", "HP83")]
public sealed class TeamTablesAuthorizationTests(HockeyPlannerWebApplicationFactory factory)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData("owner", 200, true)]
    [InlineData("admin", 200, true)]
    [InlineData("member", 200, false)]
    [InlineData("foreign", 403, false)]
    [InlineData("anonymous", 401, false)]
    public async Task Reads_UseJwtMembershipAndRole_NotLegacyOwnerQuery(string actor, int status, bool canManage)
    {
        var s = await TeamApiBaselineScenarioBuilder.CreateAsync(factory.Services);
        using var client = Client(s, actor);
        DateTime persistedCreatedAt;
        await using (var scope = factory.Services.CreateAsyncScope())
            persistedCreatedAt = await scope.ServiceProvider.GetRequiredService<AppDbContext>().TeamTables.AsNoTracking()
                .Where(x => x.Id == s.TableB.Id).Select(x => x.CreatedAt).SingleAsync(Ct);
        var paths = new[] { $"/api/teams/{s.Pair.TeamB.Id}/tables", $"/api/teams/{s.Pair.TeamB.Id}/tables/{s.TableB.Id}", $"/api/events/{s.Pair.EventB.Id}/table-protocols" };
        foreach (var query in new[] { "", $"?currentUserId={s.Pair.UserB.Id}", "?currentUserId=malformed%20actor", $"?currentUserId={s.Pair.UserA.Id}" })
        {
            foreach (var path in paths)
            {
                if (status != 200) { await Status(client.GetAsync(path + query, Ct), status); continue; }
                var dto = await Json(client.GetAsync(path + query, Ct));
                if (dto is JsonArray) dto = Assert.Single(dto.AsArray())!;
                Assert.Equal(canManage, dto["canManage"]!.GetValue<bool>());
                if (path.Contains("events")) Assert.Equal(s.ProtocolB.Id, dto["id"]!.GetValue<Guid>());
                else
                {
                    Assert.Equal(s.TableB.Id, dto["id"]!.GetValue<Guid>());
                    Assert.Equal(s.Pair.TeamB.Id, dto["teamId"]!.GetValue<Guid>());
                    Assert.Equal(s.Pair.TeamB.Name, dto["teamName"]!.GetValue<string>());
                    Assert.Equal("Table B", dto["name"]!.GetValue<string>());
                    Assert.Equal(1, dto["templateType"]!.GetValue<int>());
                    Assert.Equal(persistedCreatedAt, dto["createdAt"]!.GetValue<DateTime>());
                }
            }
        }
    }

    [Theory]
    [InlineData("owner")]
    [InlineData("admin")]
    [InlineData("member")]
    [InlineData("foreign")]
    [InlineData("anonymous")]
    public async Task Feed_ContainsOnlyJwtMemberships_WithRoleSpecificCanManage(string actor)
    {
        var s = await TeamApiBaselineScenarioBuilder.CreateAsync(factory.Services);
        using var client = Client(s, actor);
        var path = $"/api/news/tables?currentUserId={s.Pair.UserB.Id}";
        if (actor == "anonymous") { await Status(client.GetAsync(path, Ct), 401); return; }
        var actorUser = Actor(s, actor);
        // One actor is Owner/Admin in one team and a Member in another.
        if (actor != "foreign")
        {
            await using var scope = factory.Services.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.TeamMemberships.Add(new TeamMembership { TeamId = s.Pair.TeamA.Id, UserId = actorUser.Id, Role = TeamMemberRole.Member });
            await db.SaveChangesAsync(Ct);
        }
        var feed = (await Json(client.GetAsync(path, Ct))).AsArray();
        Assert.Equal(actor == "foreign" ? 1 : 2, feed.Count);
        Assert.Equal(s.TableA.Id, Assert.Single(feed, x => x!["teamId"]!.GetValue<Guid>() == s.Pair.TeamA.Id)!["id"]!.GetValue<Guid>());
        foreach (var item in feed)
            Assert.Equal(actor == "foreign" || (actor is "owner" or "admin" && item!["teamId"]!.GetValue<Guid>() == s.Pair.TeamB.Id), item!["canManage"]!.GetValue<bool>());
        Assert.DoesNotContain(feed, x => x!["id"]!.GetValue<Guid>() == (actor == "foreign" ? s.TableB.Id : Guid.Empty));
    }

    [Theory]
    [InlineData("owner", 200, false)]
    [InlineData("owner", 200, true)]
    [InlineData("admin", 200, false)]
    [InlineData("admin", 200, true)]
    [InlineData("member", 403, false)]
    [InlineData("foreign", 403, false)]
    [InlineData("anonymous", 401, false)]
    public async Task EveryMutation_UsesJwtRoleAndCreator_AndDenialsPreserveExactDatabaseState(string actor, int status, bool malformedActor)
    {
        var s = await TeamApiBaselineScenarioBuilder.CreateAsync(factory.Services);
        using var client = Client(s, actor);
        var before = await Snapshot(s);
        // A fresh local event avoids modifying the shared scenario builder/count assumptions.
        var newEvent = await AddEvent(s);
        var query = malformedActor ? "?currentUserId=malformed%20actor" : $"?currentUserId={(actor == "owner" ? s.Pair.UserA.Id : s.Pair.UserB.Id)}";
        var tablePath = $"/api/teams/{s.Pair.TeamB.Id}/tables{query}";
        var createPath = $"/api/events/{newEvent.Id}/table-protocols{query}";
        var protocolPath = $"/api/events/{s.Pair.EventB.Id}/table-protocols/{s.ProtocolB.Id}";
        var rowId = Assert.Single(s.ProtocolB.Rows).Id;
        if (status == 200)
        {
            var createdTable = await Json(client.PostAsJsonAsync(tablePath, new { name = " \t ", templateType = 1 }, Ct));
            Assert.Equal("Статистика игроков", createdTable["name"]!.GetValue<string>());
            Assert.True(createdTable["canManage"]!.GetValue<bool>());
            Assert.Equal(3, createdTable["rows"]!.AsArray().Count);
            var createdProtocol = await Json(client.PostAsJsonAsync(createPath, new { teamTableId = s.TableB.Id }, Ct));
            Assert.Equal(newEvent.Id, createdProtocol["eventId"]!.GetValue<Guid>());
            Assert.Equal(3, createdProtocol["rows"]!.AsArray().Count);
            Assert.Equal(1, createdProtocol["rows"]!.AsArray().Single(x => x!["userId"]!.GetValue<Guid>() == s.Pair.UserB.Id)!["games"]!.GetValue<int>());
            Assert.All(createdProtocol["rows"]!.AsArray().Where(x => x!["userId"]!.GetValue<Guid>() != s.Pair.UserB.Id), x => Assert.Equal(0, x!["games"]!.GetValue<int>()));
            await using (var verify = factory.Services.CreateAsyncScope())
            {
                var db = verify.ServiceProvider.GetRequiredService<AppDbContext>();
                Assert.Equal(Actor(s, actor).Id, (await db.TeamTables.SingleAsync(x => x.Id == createdTable["id"]!.GetValue<Guid>(), Ct)).CreatedByUserId);
                Assert.Equal(Actor(s, actor).Id, (await db.EventTableProtocols.SingleAsync(x => x.EventId == newEvent.Id, Ct)).CreatedByUserId);
            }
            var bulk = await Json(client.PutAsJsonAsync(protocolPath + query, new { rows = new[] { new { rowId, games = -10, goals = 1005, assists = 3 } } }, Ct));
            Assert.Equal(1002, Assert.Single(bulk["rows"]!.AsArray())!["points"]!.GetValue<int>());
            await AssertStats(s, rowId, 0, 999, 3, 1);
            var single = await Json(client.PutAsJsonAsync($"{protocolPath}/rows/{rowId}{query}", new { games = 2, goals = 4, assists = -5 }, Ct));
            Assert.Equal(4, Assert.Single(single["rows"]!.AsArray())!["points"]!.GetValue<int>());
            await AssertStats(s, rowId, 2, 4, 0, 1);
        }
        else
        {
            await Status(client.PostAsJsonAsync(tablePath, new { name = "Denied", templateType = 1 }, Ct), status);
            Assert.Equal(before, await Snapshot(s));
            await Status(client.PostAsJsonAsync(createPath, new { teamTableId = s.TableB.Id }, Ct), status);
            Assert.Equal(before, await Snapshot(s));
            await Status(client.PutAsJsonAsync(protocolPath + query, new { rows = new[] { new { rowId, games = 2, goals = 7, assists = 3 } } }, Ct), status);
            Assert.Equal(before, await Snapshot(s));
            await Status(client.PutAsJsonAsync($"{protocolPath}/rows/{rowId}{query}", new { games = 2, goals = 7, assists = 3 }, Ct), status);
            Assert.Equal(before, await Snapshot(s));
        }
    }

    [Fact]
    public async Task ValidationAndSubstitutions_RejectBeforeAnyMutation_WithRealProblemDetails()
    {
        var s = await TeamApiBaselineScenarioBuilder.CreateAsync(factory.Services);
        using var client = Client(s, "owner");
        var before = await Snapshot(s);
        var tablePath = $"/api/teams/{s.Pair.TeamB.Id}/tables";
        var root = $"/api/events/{s.Pair.EventB.Id}/table-protocols";
        var rowA = Assert.Single(s.ProtocolA.Rows).Id;
        async Task Denied(Task<HttpResponseMessage> pending, int status, string detail)
        {
            using var response = await pending;
            Assert.Equal(status, (int)response.StatusCode);
            Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
            var body = JsonNode.Parse(await response.Content.ReadAsStringAsync(Ct))!;
            Assert.Equal(detail, body["detail"]!.GetValue<string>());
            Assert.Equal(before, await Snapshot(s));
        }
        await Denied(client.PostAsJsonAsync(tablePath, new { name = "Unsupported", templateType = 99 }, Ct), 400, "Поддерживается только шаблон статистики игроков.");
        await Denied(client.PostAsJsonAsync(root, new { teamTableId = s.TableA.Id }, Ct), 404, "Основная таблица не найдена.");
        await Denied(client.PutAsJsonAsync($"{root}/{s.ProtocolA.Id}", new { rows = Array.Empty<object>() }, Ct), 404, "Протокол не найден.");
        await Denied(client.PutAsJsonAsync($"{root}/{s.ProtocolB.Id}/rows/{rowA}", new { games = 1, goals = 9, assists = 1 }, Ct), 404, "Строка протокола не найдена.");
        // Bulk rows must also belong to the selected protocol, even mixed with a valid row.
        await Denied(client.PutAsJsonAsync($"{root}/{s.ProtocolB.Id}", new { rows = new[] {
            new { rowId = Assert.Single(s.ProtocolB.Rows).Id, games = 1, goals = 9, assists = 1 },
            new { rowId = rowA, games = 1, goals = 9, assists = 1 } } }, Ct), 404, "Строка протокола не найдена.");
        var validRow = Assert.Single(s.ProtocolB.Rows).Id;
        await Denied(client.PutAsJsonAsync($"{root}/{s.ProtocolB.Id}", new { rows = new[] {
            new { rowId = validRow, games = 1, goals = 2, assists = 3 },
            new { rowId = validRow, games = 1, goals = 4, assists = 5 } } }, Ct), 400, "Строки протокола не должны повторяться.");
        await Denied(client.GetAsync($"/api/events/{s.EventA.Id}/table-protocols", Ct), 403, "Недостаточно прав");
        await Denied(client.GetAsync($"/api/events/{Guid.NewGuid()}/table-protocols", Ct), 404, "Командное мероприятие не найдено.");
        await Denied(client.GetAsync($"{tablePath}/{Guid.NewGuid()}", Ct), 404, "Таблица не найдена.");
    }

    [Fact]
    public async Task TableAndProtocolDtos_PreserveNormalizationSortingJerseysAndStats()
    {
        var s = await TeamApiBaselineScenarioBuilder.CreateAsync(factory.Services);
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var owner = await db.Users.SingleAsync(x => x.Id == s.Pair.UserB.Id, Ct);
            owner.JerseyNumber = 9;
            var member = await db.Users.SingleAsync(x => x.Id == s.Member.Id, Ct);
            member.JerseyNumber = 8;
            var membership = await db.TeamMemberships.SingleAsync(x => x.TeamId == s.Pair.TeamB.Id && x.UserId == owner.Id, Ct);
            membership.TeamJerseyNumber = 79;
            foreach (var row in await db.TeamTableRows.Where(x => x.TeamTableId == s.TableB.Id).ToListAsync(Ct))
            {
                row.Points = 5; row.Goals = 3; row.Assists = 2;
                row.Games = row.UserId == owner.Id ? 2 : 1;
            }
            await db.SaveChangesAsync(Ct);
        }
        using var client = Client(s, "member");
        var table = await Json(client.GetAsync($"/api/teams/{s.Pair.TeamB.Id}/tables/{s.TableB.Id}", Ct));
        var rows = table["rows"]!.AsArray();
        Assert.Equal(new[] { s.Admin.Id, s.Member.Id, s.Pair.UserB.Id }, rows.Select(x => x!["userId"]!.GetValue<Guid>()));
        Assert.Equal(79, rows.Last()!["jerseyNumber"]!.GetValue<int>());
        Assert.Equal(8, rows[1]!["jerseyNumber"]!.GetValue<int>());
        Assert.All(rows, x => { Assert.Equal(3, x!["goals"]!.GetValue<int>()); Assert.Equal(2, x["assists"]!.GetValue<int>()); Assert.Equal(5, x["points"]!.GetValue<int>()); });
        var protocols = await Json(client.GetAsync($"/api/events/{s.Pair.EventB.Id}/table-protocols", Ct));
        Assert.Equal(79, Assert.Single(Assert.Single(protocols.AsArray())!["rows"]!.AsArray())!["jerseyNumber"]!.GetValue<int>());
        using var ownerClient = Client(s, "owner");
        var normalized = await Json(ownerClient.PostAsJsonAsync($"/api/teams/{s.Pair.TeamB.Id}/tables?currentUserId=arbitrary", new { name = "  Team\t stats  ", templateType = 1 }, Ct));
        Assert.Equal("Team stats", normalized["name"]!.GetValue<string>());
        await using var verify = factory.Services.CreateAsyncScope();
        var context = verify.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.Equal(s.Pair.UserB.Id, (await context.TeamTables.SingleAsync(x => x.Id == normalized["id"]!.GetValue<Guid>(), Ct)).CreatedByUserId);
    }

    private HttpClient Client(TeamApiBaselineScenario s, string actor) => actor == "anonymous"
        ? factory.CreateClient() : AuthenticatedTestClientFactory.Create(factory, Actor(s, actor));

    private async Task<ScheduledEvent> AddEvent(TeamApiBaselineScenario s)
    {
        var value = new ScheduledEvent { TeamId = s.Pair.TeamB.Id, Title = "Protocol creation", StartTime = s.Pair.EventB.StartTime, DurationMinutes = 60, LocationName = "Test rink", LocationAddress = "Test address" };
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        db.AddRange(value, new Attendance { EventId = value.Id, UserId = s.Pair.UserB.Id, Status = AttendanceStatus.Confirmed });
        await db.SaveChangesAsync(Ct);
        return value;
    }

    private async Task AssertStats(TeamApiBaselineScenario s, Guid rowId, int games, int goals, int assists, int otherGames)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var row = await db.EventTableProtocolRows.AsNoTracking().SingleAsync(x => x.Id == rowId, Ct);
        Assert.Equal((games, goals, assists, goals + assists), (row.Games, row.Goals, row.Assists, row.Points));
        var aggregate = await db.TeamTableRows.AsNoTracking().SingleAsync(x => x.TeamTableId == s.TableB.Id && x.UserId == s.Pair.UserB.Id, Ct);
        Assert.Equal((games + otherGames, goals, assists, goals + assists), (aggregate.Games, aggregate.Goals, aggregate.Assists, aggregate.Points));
        Assert.Equal(0, (await db.EventTableProtocolRows.AsNoTracking().SingleAsync(x => x.EventTableProtocolId == s.ProtocolA.Id, Ct)).Points);
    }

    // Complete scalar snapshots (including IDs, creators and timestamps) from a fresh scope.
    private async Task<string> Snapshot(TeamApiBaselineScenario s)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var teamIds = new[] { s.Pair.TeamA.Id, s.Pair.TeamB.Id };
        var tables = await db.TeamTables.AsNoTracking().Where(x => teamIds.Contains(x.TeamId)).OrderBy(x => x.Id)
            .Select(x => new { x.Id, x.TeamId, x.Name, x.TemplateType, x.CreatedByUserId, x.CreatedAt, x.UpdatedAt }).ToArrayAsync(Ct);
        var rows = await db.TeamTableRows.AsNoTracking().Where(x => teamIds.Contains(x.TeamTable.TeamId)).OrderBy(x => x.Id)
            .Select(x => new { x.Id, x.TeamTableId, x.UserId, x.Games, x.Goals, x.Assists, x.Points, x.CreatedAt, x.UpdatedAt }).ToArrayAsync(Ct);
        var protocols = await db.EventTableProtocols.AsNoTracking().Where(x => teamIds.Contains(x.TeamTable.TeamId)).OrderBy(x => x.Id)
            .Select(x => new { x.Id, x.EventId, x.TeamTableId, x.CreatedByUserId, x.CreatedAt, x.UpdatedAt }).ToArrayAsync(Ct);
        var protocolRows = await db.EventTableProtocolRows.AsNoTracking().Where(x => teamIds.Contains(x.EventTableProtocol.TeamTable.TeamId)).OrderBy(x => x.Id)
            .Select(x => new { x.Id, x.EventTableProtocolId, x.UserId, x.Games, x.Goals, x.Assists, x.Points, x.CreatedAt, x.UpdatedAt }).ToArrayAsync(Ct);
        return JsonSerializer.Serialize(new { tables, rows, protocols, protocolRows });
    }
}
