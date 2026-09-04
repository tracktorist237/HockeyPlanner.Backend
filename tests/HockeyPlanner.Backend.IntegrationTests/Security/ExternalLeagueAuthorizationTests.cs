using System.Net;
using System.Net.Http.Json;
using HockeyPlanner.Backend.Core.Entities;
using HockeyPlanner.Backend.Core.Enums;
using HockeyPlanner.Backend.Infrastructure.Data;
using HockeyPlanner.Backend.IntegrationTests.Infrastructure;
using HockeyPlanner.Backend.WebAPI;
using HockeyPlanner.Backend.WebAPI.Models.ExternalLeagues;
using HockeyPlanner.Backend.WebAPI.Services;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace HockeyPlanner.Backend.IntegrationTests.Security;

[Collection(IntegrationTestCollection.Name)]
[Trait("Category", "ExternalLeagueAuthorization")]
public sealed class ExternalLeagueAuthorizationTests(HockeyPlannerWebApplicationFactory factory)
{
    [Fact]
    public async Task AnonymousSearchAndTeamLinks_AreUnauthorized()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var client = factory.CreateClient(new() { AllowAutoRedirect = false });

        using var search = await client.GetAsync(
            "/api/external-leagues/spbhl/teams/search?title=Северная",
            cancellationToken);
        using var links = await client.GetAsync($"/api/teams/{Guid.NewGuid()}/external-links", cancellationToken);

        Assert.Equal(HttpStatusCode.Unauthorized, search.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, links.StatusCode);
    }

    [Fact]
    public async Task AuthenticatedSearch_DoesNotRequireExistingTeam()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var scenario = await SeedAsync(TeamMemberRole.Member, cancellationToken);
        await using var application = CreateApplication(new ApiProvider());
        using var client = CreateAuthenticatedClient(application, scenario.User);

        using var response = await client.GetAsync(
            "/api/external-leagues/spbhl/teams/search?title=%20Северная%20",
            cancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task InvalidOrUnsupportedSearch_IsBadRequestWithoutCallingProvider()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var scenario = await SeedAsync(TeamMemberRole.Member, cancellationToken);
        var provider = new ApiProvider();
        await using var application = CreateApplication(provider);
        using var client = CreateAuthenticatedClient(application, scenario.User);

        using var shortTitle = await client.GetAsync(
            "/api/external-leagues/spbhl/teams/search?title=x",
            cancellationToken);
        using var unsupported = await client.GetAsync(
            "/api/external-leagues/999/teams/search?title=Северная",
            cancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, shortTitle.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, unsupported.StatusCode);
        Assert.Equal(0, provider.SearchCallCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ProviderTransportFailure_IsSafeBadGateway(bool timeout)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var scenario = await SeedAsync(TeamMemberRole.Member, cancellationToken);
        var provider = new ApiProvider
        {
            SearchException = timeout
                ? new TaskCanceledException("provider timeout detail")
                : new HttpRequestException("provider response detail")
        };
        await using var application = CreateApplication(provider);
        using var client = CreateAuthenticatedClient(application, scenario.User);

        using var response = await client.GetAsync(
            "/api/external-leagues/spbhl/teams/search?title=Северная",
            cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);

        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
        Assert.DoesNotContain("detail", body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task MemberCannotManageLinks_WhileOwnerCan()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var member = await SeedAsync(TeamMemberRole.Member, cancellationToken);
        var owner = await SeedAsync(TeamMemberRole.Owner, cancellationToken);
        var admin = await SeedAsync(TeamMemberRole.Admin, cancellationToken);
        var provider = new ApiProvider();
        await using var application = CreateApplication(provider);
        using var memberClient = CreateAuthenticatedClient(application, member.User);
        using var ownerClient = CreateAuthenticatedClient(application, owner.User);
        using var adminClient = CreateAuthenticatedClient(application, admin.User);

        var memberBaseUrl = $"/api/teams/{member.Team.Id}/external-links";
        using var forbiddenGet = await memberClient.GetAsync(memberBaseUrl, cancellationToken);
        using var forbiddenCreate = await memberClient.PostAsJsonAsync(memberBaseUrl, new CreateExternalLeagueLinkRequest
        {
            Provider = ExternalLeagueProvider.Spbhl,
            ExternalTeamId = Guid.NewGuid().ToString("D")
        }, cancellationToken);
        using var forbiddenDelete = await memberClient.DeleteAsync($"{memberBaseUrl}/{Guid.NewGuid()}", cancellationToken);
        using var forbiddenLinkSync = await memberClient.PostAsync(
            $"{memberBaseUrl}/{Guid.NewGuid()}/sync",
            null,
            cancellationToken);
        using var forbiddenTeamSync = await memberClient.PostAsync($"{memberBaseUrl}/sync", null, cancellationToken);
        using var forbiddenApply = await memberClient.PostAsJsonAsync(
            $"{memberBaseUrl}/{Guid.NewGuid()}/apply-profile",
            new ApplyExternalLeagueProfileRequest { UseName = true },
            cancellationToken);
        using var allowed = await ownerClient.GetAsync($"/api/teams/{owner.Team.Id}/external-links", cancellationToken);
        using var adminAllowed = await adminClient.GetAsync($"/api/teams/{admin.Team.Id}/external-links", cancellationToken);

        Assert.All(
            new[]
            {
                forbiddenGet.StatusCode,
                forbiddenCreate.StatusCode,
                forbiddenDelete.StatusCode,
                forbiddenLinkSync.StatusCode,
                forbiddenTeamSync.StatusCode,
                forbiddenApply.StatusCode
            },
            status => Assert.Equal(HttpStatusCode.Forbidden, status));
        Assert.Equal(HttpStatusCode.OK, allowed.StatusCode);
        Assert.Equal(HttpStatusCode.OK, adminAllowed.StatusCode);
        Assert.Equal(0, provider.ProfileCallCount);
    }

    [Fact]
    public async Task MissingTeam_IsNotFound()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var actor = await SeedAsync(TeamMemberRole.Owner, cancellationToken);
        await using var application = CreateApplication(new ApiProvider());
        using var client = CreateAuthenticatedClient(application, actor.User);

        using var response = await client.GetAsync($"/api/teams/{Guid.NewGuid()}/external-links", cancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task ForeignTeamUser_CannotManageLinks()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var target = await SeedAsync(TeamMemberRole.Owner, cancellationToken);
        var foreign = await SeedAsync(TeamMemberRole.Owner, cancellationToken);
        var provider = new ApiProvider();
        await using var application = CreateApplication(provider);
        using var client = CreateAuthenticatedClient(application, foreign.User);

        using var response = await client.GetAsync($"/api/teams/{target.Team.Id}/external-links", cancellationToken);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal(0, provider.ProfileCallCount);
    }

    private WebApplicationFactory<Program> CreateApplication(IExternalLeagueProvider provider) =>
        factory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<IExternalLeagueProvider>();
            services.AddSingleton(provider);
        }));

    private async Task<(User User, Team Team)> SeedAsync(TeamMemberRole role, CancellationToken cancellationToken)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var user = new User { FirstName = "External", LastName = "API", Role = UserRole.Player, AppRole = AppRole.User };
        var team = new Team
        {
            Name = $"External API {Guid.NewGuid():N}",
            InviteCode = Guid.NewGuid().ToString("N")[..20],
            Visibility = TeamVisibility.Private,
            CreatedByUserId = user.Id
        };
        context.AddRange(user, team);
        context.TeamMemberships.Add(new TeamMembership { Team = team, User = user, Role = role });
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

    private sealed class ApiProvider : IExternalLeagueProvider
    {
        public ExternalLeagueProvider Provider => ExternalLeagueProvider.Spbhl;
        public int SearchCallCount { get; private set; }
        public int ProfileCallCount { get; private set; }
        public Exception? SearchException { get; init; }
        public Task<IReadOnlyCollection<ExternalTeamSearchItem>> SearchTeamsAsync(string title, CancellationToken cancellationToken)
        {
            SearchCallCount++;
            return SearchException is null
                ? Task.FromResult<IReadOnlyCollection<ExternalTeamSearchItem>>([new()
            {
                Provider = Provider,
                ExternalTeamId = Guid.NewGuid().ToString("D"),
                Name = title
            }])
                : Task.FromException<IReadOnlyCollection<ExternalTeamSearchItem>>(SearchException);
        }
        public Task<ExternalTeamProfile?> GetTeamProfileAsync(string externalTeamId, CancellationToken cancellationToken)
        {
            ProfileCallCount++;
            return Task.FromResult<ExternalTeamProfile?>(null);
        }
        public Task<IReadOnlyCollection<ExternalMatch>> GetTeamScheduleAsync(string externalTeamId, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyCollection<ExternalMatch>>([]);
        public Task<ExternalMatchDetails?> GetMatchDetailsAsync(
            string externalCompetitionId,
            string externalMatchId,
            CancellationToken cancellationToken) =>
            Task.FromResult<ExternalMatchDetails?>(null);
    }
}
