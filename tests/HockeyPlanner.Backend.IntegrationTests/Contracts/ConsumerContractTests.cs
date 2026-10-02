using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using HockeyPlanner.Backend.Core.Entities;
using HockeyPlanner.Backend.Core.Enums;
using HockeyPlanner.Backend.Core.Exceptions;
using HockeyPlanner.Backend.Infrastructure.Data;
using HockeyPlanner.Backend.IntegrationTests.Infrastructure;
using HockeyPlanner.Backend.WebAPI.Models.Events;
using HockeyPlanner.Backend.WebAPI.Services;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace HockeyPlanner.Backend.IntegrationTests.Contracts;

[Collection(IntegrationTestCollection.Name)]
public sealed class ConsumerContractTests(HockeyPlannerWebApplicationFactory factory)
{
    [Fact]
    public async Task RealHttpResponses_MatchVersionedConsumerContract()
    {
        var ct = TestContext.Current.CancellationToken;
        var instant = new DateTime(2030, 1, 15, 18, 0, 0, DateTimeKind.Utc);
        var user = FixedId(new User { FirstName = "Contract", LastName = "Player",
            Email = "contract@test.invalid", EmailConfirmed = true }, 1);
        var team = FixedId(new Team { Name = "Contract team", CreatedByUserId = user.Id,
            Visibility = TeamVisibility.Private, InviteCode = "CONTRACT" }, 2);
        var source = FixedId(new ScheduledEvent { TeamId = team.Id, Title = "Contract source",
            StartTime = instant, DurationMinutes = 60, Status = EventStatus.Scheduled,
            LocationName = "Test arena", LocationAddress = "" }, 3);
        var target = FixedId(new ScheduledEvent { TeamId = team.Id, Title = "Contract target",
            StartTime = instant, DurationMinutes = 60, Status = EventStatus.Scheduled,
            LocationName = "Test arena", LocationAddress = "" }, 4);
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.AddRange(user, team, source, target,
                new TeamMembership { TeamId = team.Id, UserId = user.Id, Role = TeamMemberRole.Owner },
                new Attendance { EventId = source.Id, UserId = user.Id, Status = AttendanceStatus.Confirmed },
                FixedId(new Notification { UserId = user.Id, Title = "Contract notification",
                    Body = "Synthetic test content", Type = NotificationType.EventPublished,
                    Category = NotificationCategory.AttendanceRequired, Url = $"/events/{target.Id}",
                    CreatedAt = instant, UpdatedAt = instant }, 5),
                FixedId(new TeamNews { TeamId = team.Id, AuthorUserId = user.Id, Title = "Contract news",
                    Body = "Synthetic team content", CreatedAt = instant, UpdatedAt = instant }, 6));
            await db.SaveChangesAsync(ct);
        }
        using var client = AuthenticatedTestClientFactory.Create(factory, user);
        using var foreign = AuthenticatedTestClientFactory.Create(factory, new User { FirstName = "Foreign" });
        var bundle = new JsonObject();
        async Task Capture(string name, HttpResponseMessage response, int status)
        {
            using (response)
            {
                Assert.Equal(status, (int)response.StatusCode);
                var body = JsonNode.Parse(await response.Content.ReadAsStringAsync(ct))!;
                if (status >= 400)
                {
                    Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
                    Assert.Equal(status, body["status"]!.GetValue<int>());
                    Assert.Equal(Assert.Single(response.Headers.GetValues("X-Correlation-ID")), body["traceId"]!.GetValue<string>());
                    // Only the unpredictable correlation ID is normalized; DTO keys/values stay on the wire.
                    body["traceId"] = "contract-trace";
                }
                bundle[name] = new JsonObject { ["status"] = status, ["body"] = body };
            }
        }
        await Capture("notFound", await client.GetAsync("/api/contract-missing-route", ct), 404);
        await Capture("unauthorized", await factory.Client.GetAsync("/api/notifications", ct), 401);
        await Capture("authError", await factory.Client.PostAsJsonAsync("/api/auth/login",
            new { email = "nobody@test.invalid", password = "invalid-test-input" }, ct), 401);
        await Capture("validation", await factory.Client.PostAsync("/api/auth/login",
            new StringContent("{\"email\":", Encoding.UTF8, "application/json"), ct), 400);
        await Capture("attendanceConflict", await client.PostAsJsonAsync($"/api/events/{target.Id}/attendance/{user.Id}",
            new { status = 2, ignoreConflicts = false }, ct), 409);
        await Capture("transferError", await client.PostAsJsonAsync($"/api/events/{source.Id}/transfer/preview",
            new { targetEventId = source.Id, attendanceTransferMode = 2 }, ct), 400);
        await Capture("transferPreview", await client.PostAsJsonAsync($"/api/events/{source.Id}/transfer/preview",
            new { targetEventId = target.Id, attendanceTransferMode = 2 }, ct), 200);
        await Capture("notifications", await client.GetAsync("/api/notifications?take=8", ct), 200);
        await Capture("preferences", await client.GetAsync("/api/notifications/preferences/me", ct), 200);

        // HP-80: authenticated DTOs and genuine TeamsController security/validation responses.
        await Capture("team", await client.GetAsync($"/api/teams/{team.Id}", ct), 200);
        await Capture("teamMembers", await client.GetAsync($"/api/teams/{team.Id}/members", ct), 200);
        await Capture("teamNews", await client.GetAsync($"/api/teams/{team.Id}/news", ct), 200);
        await Capture("teamUnauthorized", await factory.Client.GetAsync($"/api/teams?currentUserId={user.Id}", ct), 401);
        await Capture("teamBadRequest", await client.PostAsJsonAsync("/api/teams",
            new { name = "", visibility = 2 }, ct), 400);
        await Capture("teamForbidden", await foreign.GetAsync($"/api/teams/{team.Id}?currentUserId={user.Id}", ct), 403);
        await Capture("teamNotFound", await client.GetAsync($"/api/teams/{Id(99)}", ct), 404);
        await Capture("teamConflict", await client.PostAsJsonAsync("/api/teams",
            new { name = team.Name, visibility = 2 }, ct), 409);

        // Fault injection at the use-case boundary still traverses the real controller/middleware/serializer.
        using var conflictHost = factory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<IEventDataTransferService>();
            services.AddSingleton<IEventDataTransferService, ConcurrentTransfer>();
        }));
        using var conflictClient = conflictHost.CreateClient();
        conflictClient.DefaultRequestHeaders.Authorization = client.DefaultRequestHeaders.Authorization;
        await Capture("transferConflict", await conflictClient.PostAsJsonAsync($"/api/events/{source.Id}/transfer",
            new { targetEventId = target.Id, attendance = true }, ct), 409);

        var json = bundle.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + "\n";
        var output = Environment.GetEnvironmentVariable("HP_CONTRACT_OUTPUT");
        if (!string.IsNullOrEmpty(output))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!);
            await File.WriteAllTextAsync(output, json, ct);
        }
        if (Environment.GetEnvironmentVariable("HP_UPDATE_CONTRACT") == "1") return;
        var expected = JsonNode.Parse(await File.ReadAllTextAsync(
            Path.Combine(AppContext.BaseDirectory, "Contracts", "api-contract.json"), ct));
        Assert.True(JsonNode.DeepEquals(expected, bundle),
            "Real HTTP contract drifted. Export HP_CONTRACT_OUTPUT and review backend + frontend consumer changes together.");
    }

    private static Guid Id(int number) => Guid.Parse($"71000000-0000-0000-0000-{number:000000000000}");

    private static T FixedId<T>(T entity, int number) where T : HockeyPlanner.Backend.Core.Entities.Base.Entity
    {
        // Stable seed IDs only. Never rewrite serializer output to conceal contract differences.
        typeof(HockeyPlanner.Backend.Core.Entities.Base.Entity).GetProperty("Id")!.SetValue(entity, Id(number));
        return entity;
    }

    private sealed class ConcurrentTransfer : IEventDataTransferService
    {
        public Task<AttendanceTransferPreviewDto> PreviewAttendanceAsync(Guid sourceEventId, Guid actorUserId,
            PreviewAttendanceTransferRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task TransferAsync(Guid sourceEventId, Guid actorUserId, TransferEventDataRequest request,
            CancellationToken cancellationToken) => throw new ConflictException("Данные мероприятия изменились.");
    }
}
