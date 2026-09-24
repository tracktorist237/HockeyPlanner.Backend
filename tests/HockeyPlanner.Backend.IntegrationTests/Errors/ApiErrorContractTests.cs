using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using HockeyPlanner.Backend.IntegrationTests.Fixtures;
using HockeyPlanner.Backend.IntegrationTests.Infrastructure;

namespace HockeyPlanner.Backend.IntegrationTests.Errors;

[Collection(IntegrationTestCollection.Name)]
public sealed class ApiErrorContractTests(HockeyPlannerWebApplicationFactory factory)
{
    [Fact]
    public async Task AuthenticationChallenge_UsesProblemDetails_AndKeepsBearerChallenge()
    {
        using var response = await factory.Client.GetAsync("/api/notifications", TestContext.Current.CancellationToken);
        await AssertProblemAsync(response, HttpStatusCode.Unauthorized);
        Assert.Contains(response.Headers.WwwAuthenticate, value => value.Scheme == "Bearer");
    }

    [Fact]
    public async Task MissingRoute_IsSafeCorrelatedNotFound()
    {
        using var response = await factory.Client.GetAsync("/api/no-such-route?password=PRIVATE", TestContext.Current.CancellationToken);
        var problem = await AssertProblemAsync(response, HttpStatusCode.NotFound);
        Assert.DoesNotContain("PRIVATE", problem.ToString());
    }

    [Fact]
    public async Task EventAccess_StillDistinguishesAnonymousAndForeignUser()
    {
        var token = TestContext.Current.CancellationToken;
        var scenario = await TwoTeamSecurityScenarioBuilder.CreateAsync(factory.Services, token);
        using var anonymous = await factory.Client.GetAsync($"/api/events/{scenario.EventB.Id}", token);
        await AssertProblemAsync(anonymous, HttpStatusCode.Unauthorized);
        using var client = AuthenticatedTestClientFactory.Create(factory, scenario.UserA);
        using var forbidden = await client.GetAsync($"/api/events/{scenario.EventB.Id}", token);
        await AssertProblemAsync(forbidden, HttpStatusCode.Forbidden);
        using var missing = await client.GetAsync($"/api/events/{Guid.NewGuid()}", token);
        await AssertProblemAsync(missing, HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task TransferValidation_UsesCurrentIdentity_AndReturnsControlledProblem()
    {
        var token = TestContext.Current.CancellationToken;
        var scenario = await TwoTeamSecurityScenarioBuilder.CreateAsync(factory.Services, token);
        using var client = AuthenticatedTestClientFactory.Create(factory, scenario.UserB);
        using var response = await client.PostAsJsonAsync($"/api/events/{scenario.EventB.Id}/transfer/preview",
            new { targetEventId = scenario.EventB.Id, attendanceTransferMode = 2 }, token);
        await AssertProblemAsync(response, HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task MalformedJson_IsValidationProblem_NotInternalFailure()
    {
        var token = TestContext.Current.CancellationToken;
        using var response = await factory.Client.PostAsync("/api/auth/login",
            new StringContent("{\"email\":", Encoding.UTF8, "application/json"), token);
        var problem = await AssertProblemAsync(response, HttpStatusCode.BadRequest);
        Assert.True(problem.TryGetProperty("errors", out var errors));
        Assert.NotEmpty(errors.EnumerateObject());
        Assert.DoesNotContain("JsonException", problem.ToString());
    }

    private static async Task<JsonElement> AssertProblemAsync(HttpResponseMessage response, HttpStatusCode status)
    {
        Assert.Equal(status, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        Assert.Equal((int)status, problem.GetProperty("status").GetInt32());
        Assert.Equal(problem.GetProperty("detail").GetString(), problem.GetProperty("message").GetString());
        Assert.Equal(problem.GetProperty("traceId").GetString(), Assert.Single(response.Headers.GetValues("X-Correlation-ID")));
        return problem;
    }
}
