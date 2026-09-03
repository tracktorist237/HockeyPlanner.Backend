using System.Net;
using System.Net.Http.Json;
using HockeyPlanner.Backend.Core.Entities;
using HockeyPlanner.Backend.Core.Enums;
using HockeyPlanner.Backend.Infrastructure.Data;
using HockeyPlanner.Backend.IntegrationTests.Infrastructure;
using HockeyPlanner.Backend.WebAPI;
using HockeyPlanner.Backend.WebAPI.Models.Spbhl;
using HockeyPlanner.Backend.WebAPI.Services;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace HockeyPlanner.Backend.IntegrationTests.Security;

[Collection(IntegrationTestCollection.Name)]
[Trait("Category", "SpbhlTeamManagement")]
public sealed class TeamSpbhlAuthorizationTests(HockeyPlannerWebApplicationFactory factory)
{
    [Theory]
    [InlineData("GET", "")]
    [InlineData("GET", "/search?title=Ладога")]
    [InlineData("POST", "/link")]
    [InlineData("DELETE", "")]
    [InlineData("POST", "/sync")]
    public async Task AnonymousManagementEndpoint_IsUnauthorized(string method, string suffix)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var client = factory.CreateClient(new() { AllowAutoRedirect = false });
        using var request = CreateRequest(method, $"/api/teams/{Guid.NewGuid()}/spbhl{suffix}");

        using var response = await client.SendAsync(request, cancellationToken);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Theory]
    [InlineData(TeamMemberRole.Member)]
    [InlineData(null)]
    public async Task NonManager_IsForbiddenForEveryManagementEndpoint(TeamMemberRole? role)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var scenario = await SeedAsync(role, cancellationToken);
        var clientFake = new ApiFakeSpbhlClient();
        var syncFake = new ApiFakeSyncService();
        await using var application = CreateApplication(clientFake, syncFake);
        using var client = CreateAuthenticatedClient(application, scenario.User);
        var routes = new (string Method, string Suffix)[]
        {
            ("GET", ""),
            ("GET", "/search?title=Ладога"),
            ("POST", "/link"),
            ("DELETE", ""),
            ("POST", "/sync")
        };

        foreach (var route in routes)
        {
            using var request = CreateRequest(route.Method, $"/api/teams/{scenario.Team.Id}/spbhl{route.Suffix}");
            using var response = await client.SendAsync(request, cancellationToken);
            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        }

        Assert.Equal(0, clientFake.SearchCallCount);
        Assert.Equal(0, syncFake.CallCount);
    }

    [Theory]
    [InlineData(TeamMemberRole.Owner)]
    [InlineData(TeamMemberRole.Admin)]
    public async Task OwnerAndAdmin_AreAllowed_AndSpoofedCurrentUserIdIsIgnored(TeamMemberRole role)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var scenario = await SeedAsync(role, cancellationToken);
        var clientFake = new ApiFakeSpbhlClient();
        var syncFake = new ApiFakeSyncService();
        await using var application = CreateApplication(clientFake, syncFake);
        using var client = CreateAuthenticatedClient(application, scenario.User);

        using var status = await client.GetAsync(
            $"/api/teams/{scenario.Team.Id}/spbhl?currentUserId={Guid.NewGuid()}",
            cancellationToken);
        using var search = await client.GetAsync(
            $"/api/teams/{scenario.Team.Id}/spbhl/search?title=%20Ладога%20&currentUserId={Guid.NewGuid()}",
            cancellationToken);
        using var bindRequest = CreateRequest("POST", $"/api/teams/{scenario.Team.Id}/spbhl/link");
        using var bind = await client.SendAsync(bindRequest, cancellationToken);
        using var sync = await client.PostAsync($"/api/teams/{scenario.Team.Id}/spbhl/sync", null, cancellationToken);
        using var unbind = await client.DeleteAsync($"/api/teams/{scenario.Team.Id}/spbhl", cancellationToken);

        Assert.Equal(HttpStatusCode.OK, status.StatusCode);
        Assert.Equal(HttpStatusCode.OK, search.StatusCode);
        Assert.Equal(HttpStatusCode.OK, bind.StatusCode);
        Assert.Equal(HttpStatusCode.OK, sync.StatusCode);
        Assert.Equal(HttpStatusCode.OK, unbind.StatusCode);
        Assert.Equal("Ладога", clientFake.LastSearchTitle);
        Assert.Equal(2, syncFake.CallCount);
    }

    private WebApplicationFactory<Program> CreateApplication(ISpbhlClient client, ISpbhlTeamSyncService sync) =>
        factory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<ISpbhlClient>();
            services.RemoveAll<ISpbhlTeamSyncService>();
            services.AddSingleton(client);
            services.AddSingleton(sync);
        }));

    private async Task<(User User, Team Team)> SeedAsync(
        TeamMemberRole? role,
        CancellationToken cancellationToken,
        bool linked = false)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var user = new User { FirstName = "API", LastName = "Actor", Role = UserRole.Player, AppRole = AppRole.User };
        var team = new Team
        {
            Name = $"API management {Guid.NewGuid():N}",
            InviteCode = Guid.NewGuid().ToString("N")[..20],
            Visibility = TeamVisibility.Private,
            CreatedByUserId = user.Id,
            SpbhlTeamName = linked ? "Ладога" : null
        };
        team.SpbhlTeamId = linked ? team.Id : null;
        context.AddRange(user, team);
        if (role.HasValue)
        {
            context.TeamMemberships.Add(new TeamMembership { Team = team, User = user, Role = role.Value });
        }
        await context.SaveChangesAsync(cancellationToken);
        return (user, team);
    }

    private static HttpClient CreateAuthenticatedClient(WebApplicationFactory<Program> application, User user)
    {
        using var scope = application.Services.CreateScope();
        var token = scope.ServiceProvider.GetRequiredService<IAuthTokenService>().CreateAccessToken(user);
        var client = application.CreateClient(new() { AllowAutoRedirect = false });
        client.DefaultRequestHeaders.Authorization = new("Bearer", token);
        return client;
    }

    private static HttpRequestMessage CreateRequest(string method, string path)
    {
        var request = new HttpRequestMessage(new HttpMethod(method), path);
        if (method == "POST" && path.EndsWith("/link", StringComparison.Ordinal))
        {
            request.Content = JsonContent.Create(new BindSpbhlTeamRequest
            {
                SpbhlTeamId = ExternalTeamId,
                SpbhlTeamName = "Ладога"
            });
        }
        return request;
    }

    private static readonly Guid ExternalTeamId = Guid.Parse("8d7c1823-0e26-4c7c-bbcb-9ab84b2fc953");

    private sealed class ApiFakeSpbhlClient : ISpbhlClient
    {
        public int SearchCallCount { get; private set; }
        public string? LastSearchTitle { get; private set; }
        public Task<IReadOnlyCollection<SpbhlTeamSearchItem>> SearchTeamsAsync(string? title, CancellationToken cancellationToken)
        {
            SearchCallCount++;
            LastSearchTitle = title;
            return Task.FromResult<IReadOnlyCollection<SpbhlTeamSearchItem>>([new()
            {
                TeamId = ExternalTeamId,
                Name = "Ладога",
                ProfileUrl = $"https://spbhl.ru/Team?TeamID={ExternalTeamId}"
            }]);
        }
        public Task<IReadOnlyCollection<SpbhlMatchItem>> GetTeamScheduleAsync(Guid teamId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private sealed class ApiFakeSyncService : ISpbhlTeamSyncService
    {
        public int CallCount { get; private set; }
        public Task<SpbhlTeamSyncResult> SyncTeamAsync(Guid teamId, CancellationToken cancellationToken)
        {
            CallCount++;
            return Task.FromResult(new SpbhlTeamSyncResult
            {
                TeamId = teamId,
                SpbhlTeamId = ExternalTeamId,
                SyncedAt = DateTime.UtcNow
            });
        }
    }
}
