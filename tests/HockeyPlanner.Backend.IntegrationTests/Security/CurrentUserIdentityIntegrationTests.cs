using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using HockeyPlanner.Backend.Application.Abstractions.Identity;
using HockeyPlanner.Backend.Core.Entities;
using HockeyPlanner.Backend.IntegrationTests.Fixtures;
using HockeyPlanner.Backend.IntegrationTests.Infrastructure;
using HockeyPlanner.Backend.Infrastructure.Data;
using HockeyPlanner.Backend.WebAPI;
using HockeyPlanner.Backend.WebAPI.Models.Admin;
using HockeyPlanner.Backend.WebAPI.Models.Auth;
using HockeyPlanner.Backend.WebAPI.Services;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace HockeyPlanner.Backend.IntegrationTests.Security;

[Collection(IntegrationTestCollection.Name)]
[Trait("Category", "JwtIdentity")]
public sealed class CurrentUserIdentityIntegrationTests
{
    private readonly HockeyPlannerWebApplicationFactory _application;

    public CurrentUserIdentityIntegrationTests(HockeyPlannerWebApplicationFactory application)
    {
        _application = application;
    }

    [Fact]
    public async Task Me_WithoutJwt_ReturnsUnauthorized()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var client = CreateAnonymousClient();

        using var response = await client.GetAsync("/api/auth/me", cancellationToken);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Me_WithRealJwt_PreservesSuccessfulContract()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var scenario = await TwoTeamSecurityScenarioBuilder.CreateAsync(
            _application.Services,
            cancellationToken);
        using var client = AuthenticatedTestClientFactory.Create(_application, scenario.UserB);

        using var response = await client.GetAsync("/api/auth/me", cancellationToken);
        var body = await response.Content.ReadFromJsonAsync<AuthUserResponse>(cancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.NotNull(body);
        Assert.Equal(scenario.UserB.Id, body.Id);
        Assert.Equal(scenario.UserB.Email, body.Email);
    }

    [Fact]
    public async Task Me_UsesInjectedCurrentUser_InsteadOfJwtPrincipalDirectly()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var scenario = await TwoTeamSecurityScenarioBuilder.CreateAsync(
            _application.Services,
            cancellationToken);
        var spy = new SpyCurrentUser(isAuthenticated: true, scenario.UserB.Id);
        using var spyApplication = CreateApplicationWithCurrentUser(spy);
        using var client = CreateAuthenticatedClient(spyApplication, scenario.UserA);

        using var response = await client.GetAsync("/api/auth/me", cancellationToken);
        var body = await response.Content.ReadFromJsonAsync<AuthUserResponse>(cancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.NotNull(body);
        Assert.NotEqual(scenario.UserA.Id, body.Id);
        Assert.Equal(scenario.UserB.Id, body.Id);
        Assert.True(spy.UserIdReadCount > 0);
    }

    [Fact]
    public async Task CreateReport_WithRealJwt_PersistsJwtUserId()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var scenario = await TwoTeamSecurityScenarioBuilder.CreateAsync(
            _application.Services,
            cancellationToken);
        using var client = AuthenticatedTestClientFactory.Create(_application, scenario.UserA);

        var reportId = await CreateReportAsync(client, "Authenticated report", cancellationToken);

        await using var scope = _application.Services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var persistedUserId = await dbContext.AppReports
            .AsNoTracking()
            .Where(report => report.Id == reportId)
            .Select(report => report.UserId)
            .SingleAsync(cancellationToken);
        Assert.Equal(scenario.UserA.Id, persistedUserId);
    }

    [Fact]
    public async Task CreateReport_UsesInjectedCurrentUser_InsteadOfJwtPrincipalDirectly()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var scenario = await TwoTeamSecurityScenarioBuilder.CreateAsync(
            _application.Services,
            cancellationToken);
        var spy = new SpyCurrentUser(isAuthenticated: true, scenario.UserB.Id);
        using var spyApplication = CreateApplicationWithCurrentUser(spy);
        using var client = CreateAuthenticatedClient(spyApplication, scenario.UserA);

        var reportId = await CreateReportAsync(client, "Injected current user report", cancellationToken);

        await using var scope = spyApplication.Services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var persistedUserId = await dbContext.AppReports
            .AsNoTracking()
            .Where(report => report.Id == reportId)
            .Select(report => report.UserId)
            .SingleAsync(cancellationToken);
        Assert.NotEqual(scenario.UserA.Id, persistedUserId);
        Assert.Equal(scenario.UserB.Id, persistedUserId);
        Assert.True(spy.UserIdReadCount > 0);
    }

    [Fact]
    public async Task CreateReport_WithoutJwt_PersistsNullUserId()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var client = CreateAnonymousClient();

        var reportId = await CreateReportAsync(client, "Anonymous report", cancellationToken);

        await using var scope = _application.Services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var persistedUserId = await dbContext.AppReports
            .AsNoTracking()
            .Where(report => report.Id == reportId)
            .Select(report => report.UserId)
            .SingleAsync(cancellationToken);
        Assert.Null(persistedUserId);
    }

    private HttpClient CreateAnonymousClient()
    {
        return _application.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
        });
    }

    private WebApplicationFactory<Program> CreateApplicationWithCurrentUser(SpyCurrentUser spy)
    {
        return _application.WithWebHostBuilder(builder =>
        {
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<ICurrentUser>();
                services.AddSingleton<ICurrentUser>(spy);
            });
        });
    }

    private static HttpClient CreateAuthenticatedClient(
        WebApplicationFactory<Program> application,
        User user)
    {
        using var scope = application.Services.CreateScope();
        var tokenService = scope.ServiceProvider.GetRequiredService<IAuthTokenService>();
        var accessToken = tokenService.CreateAccessToken(user);
        var client = application.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
        });
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

        return client;
    }

    private static async Task<Guid> CreateReportAsync(
        HttpClient client,
        string title,
        CancellationToken cancellationToken)
    {
        using var response = await client.PostAsJsonAsync(
            "/api/reports",
            new CreateAppReportRequest
            {
                Title = title,
                Message = "JWT identity integration test report.",
            },
            cancellationToken);
        var body = await response.Content.ReadFromJsonAsync<AppReportDto>(cancellationToken);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.NotNull(body);
        return body.Id;
    }
}
