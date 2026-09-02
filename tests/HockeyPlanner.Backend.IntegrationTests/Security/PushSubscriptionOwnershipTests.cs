using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using HockeyPlanner.Backend.Core.Entities;
using HockeyPlanner.Backend.Core.Enums;
using HockeyPlanner.Backend.Infrastructure.Data;
using HockeyPlanner.Backend.IntegrationTests.Fixtures;
using HockeyPlanner.Backend.IntegrationTests.Infrastructure;
using HockeyPlanner.Backend.WebAPI.Options;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

namespace HockeyPlanner.Backend.IntegrationTests.Security;

[Collection(IntegrationTestCollection.Name)]
public sealed class PushSubscriptionOwnershipTests
{
    private readonly HockeyPlannerWebApplicationFactory _application;

    public PushSubscriptionOwnershipTests(HockeyPlannerWebApplicationFactory application)
    {
        _application = application;
    }

    [Fact]
    public async Task FreshSubscribe_BelongsToJwtUser_AndIsActive()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var scenario = await TwoUserIdentityScenarioBuilder.CreateAsync(_application.Services, cancellationToken);
        var input = NewSubscription();
        using var client = AuthenticatedTestClientFactory.Create(_application, scenario.UserA);

        using var response = await Subscribe(client, input, cancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var persisted = await LoadByEndpoint(input.Endpoint, cancellationToken);
        Assert.Equal(scenario.UserA.Id, persisted.UserId);
        Assert.Equal(input.P256dh, persisted.P256dhKey);
        Assert.Equal(input.Auth, persisted.AuthKey);
        Assert.True(persisted.IsActive);
        Assert.Equal(1, await CountByEndpoint(input.Endpoint, cancellationToken));
    }

    [Fact]
    public async Task RepeatSameOwnerAndKeys_IsIdempotent()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var scenario = await TwoUserIdentityScenarioBuilder.CreateAsync(_application.Services, cancellationToken);
        var input = NewSubscription();
        using var client = AuthenticatedTestClientFactory.Create(_application, scenario.UserA);

        using var first = await Subscribe(client, input, cancellationToken);
        using var second = await Subscribe(client, input, cancellationToken);

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        Assert.Equal(1, await CountByEndpoint(input.Endpoint, cancellationToken));
        Assert.Equal(scenario.UserA.Id, (await LoadByEndpoint(input.Endpoint, cancellationToken)).UserId);
    }

    [Fact]
    public async Task SameOwner_CanRefreshKeys()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var scenario = await TwoUserIdentityScenarioBuilder.CreateAsync(_application.Services, cancellationToken);
        var input = NewSubscription();
        using var client = AuthenticatedTestClientFactory.Create(_application, scenario.UserA);
        using var initial = await Subscribe(client, input, cancellationToken);
        var refreshed = input with { P256dh = "p256dh-refreshed", Auth = "auth-refreshed" };

        using var response = await Subscribe(client, refreshed, cancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var persisted = await LoadByEndpoint(input.Endpoint, cancellationToken);
        Assert.Equal(scenario.UserA.Id, persisted.UserId);
        Assert.Equal(refreshed.P256dh, persisted.P256dhKey);
        Assert.Equal(refreshed.Auth, persisted.AuthKey);
        Assert.True(persisted.IsActive);
        Assert.Equal(1, await CountByEndpoint(input.Endpoint, cancellationToken));
    }

    [Fact]
    [Trait("Category", "SecurityExpectation")]
    public async Task LegacyBodyUserId_CannotOverrideJwtOwner()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var scenario = await TwoUserIdentityScenarioBuilder.CreateAsync(_application.Services, cancellationToken);
        var input = NewSubscription();
        using var client = AuthenticatedTestClientFactory.Create(_application, scenario.UserA);
        var payload = new
        {
            endpoint = input.Endpoint,
            keys = new { p256dh = input.P256dh, auth = input.Auth },
            userId = scenario.UserB.Id,
        };

        using var response = await client.PostAsJsonAsync("/api/push/subscribe", payload, cancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(scenario.UserA.Id, (await LoadByEndpoint(input.Endpoint, cancellationToken)).UserId);
    }

    [Fact]
    public async Task ForeignOwner_WithSameKeys_CanRebindExistingEndpoint()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var scenario = await TwoUserIdentityScenarioBuilder.CreateAsync(_application.Services, cancellationToken);
        var input = NewSubscription();
        using var userAClient = AuthenticatedTestClientFactory.Create(_application, scenario.UserA);
        using var userBClient = AuthenticatedTestClientFactory.Create(_application, scenario.UserB);
        using var initial = await Subscribe(userAClient, input, cancellationToken);

        using var response = await Subscribe(userBClient, input, cancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var persisted = await LoadByEndpoint(input.Endpoint, cancellationToken);
        Assert.Equal(scenario.UserB.Id, persisted.UserId);
        Assert.True(persisted.IsActive);
        Assert.Equal(1, await CountByEndpoint(input.Endpoint, cancellationToken));
    }

    [Theory]
    [InlineData("foreign-p256dh", "auth-key")]
    [InlineData("p256dh-key", "foreign-auth")]
    [Trait("Category", "SecurityExpectation")]
    public async Task ForeignOwner_WithDifferentKeys_GetsConflict_AndDatabaseIsUnchanged(
        string p256dh,
        string auth)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var scenario = await TwoUserIdentityScenarioBuilder.CreateAsync(_application.Services, cancellationToken);
        var input = NewSubscription();
        using var userAClient = AuthenticatedTestClientFactory.Create(_application, scenario.UserA);
        using var userBClient = AuthenticatedTestClientFactory.Create(_application, scenario.UserB);
        using var initial = await Subscribe(userAClient, input, cancellationToken);
        var before = await LoadByEndpoint(input.Endpoint, cancellationToken);

        using var response = await Subscribe(
            userBClient,
            input with { P256dh = p256dh, Auth = auth },
            cancellationToken);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal(before, await LoadByEndpoint(input.Endpoint, cancellationToken));
        Assert.Equal(1, await CountByEndpoint(input.Endpoint, cancellationToken));
    }

    [Fact]
    public async Task LegacyUnownedSubscription_WithSameKeys_CanBeClaimed()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var scenario = await TwoUserIdentityScenarioBuilder.CreateAsync(_application.Services, cancellationToken);
        var input = NewSubscription();
        await SeedSubscription(input, userId: null, isActive: false, cancellationToken);
        using var client = AuthenticatedTestClientFactory.Create(_application, scenario.UserA);

        using var response = await Subscribe(client, input, cancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var persisted = await LoadByEndpoint(input.Endpoint, cancellationToken);
        Assert.Equal(scenario.UserA.Id, persisted.UserId);
        Assert.True(persisted.IsActive);
    }

    [Fact]
    public async Task LegacyUnownedSubscription_WithDifferentKeys_GetsConflict()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var scenario = await TwoUserIdentityScenarioBuilder.CreateAsync(_application.Services, cancellationToken);
        var input = NewSubscription();
        await SeedSubscription(input, userId: null, isActive: false, cancellationToken);
        var before = await LoadByEndpoint(input.Endpoint, cancellationToken);
        using var client = AuthenticatedTestClientFactory.Create(_application, scenario.UserA);

        using var response = await Subscribe(
            client,
            input with { Auth = "different-auth" },
            cancellationToken);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal(before, await LoadByEndpoint(input.Endpoint, cancellationToken));
    }

    [Fact]
    public async Task OwnUnsubscribe_IsIdempotent()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var scenario = await TwoUserIdentityScenarioBuilder.CreateAsync(_application.Services, cancellationToken);
        var input = NewSubscription();
        await SeedSubscription(input, scenario.UserA.Id, isActive: true, cancellationToken);
        using var client = AuthenticatedTestClientFactory.Create(_application, scenario.UserA);

        using var first = await Unsubscribe(client, input.Endpoint, cancellationToken);
        using var second = await Unsubscribe(client, input.Endpoint, cancellationToken);

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        var persisted = await LoadByEndpoint(input.Endpoint, cancellationToken);
        Assert.False(persisted.IsActive);
        Assert.NotNull(persisted.RevokedAt);
    }

    [Fact]
    [Trait("Category", "SecurityExpectation")]
    public async Task ForeignUnsubscribe_ReturnsSuccess_ButDoesNotChangeSubscription()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var scenario = await TwoUserIdentityScenarioBuilder.CreateAsync(_application.Services, cancellationToken);
        var input = NewSubscription();
        await SeedSubscription(input, scenario.UserB.Id, isActive: true, cancellationToken);
        var before = await LoadByEndpoint(input.Endpoint, cancellationToken);
        using var client = AuthenticatedTestClientFactory.Create(_application, scenario.UserA);

        using var response = await Unsubscribe(client, input.Endpoint, cancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(before, await LoadByEndpoint(input.Endpoint, cancellationToken));
    }

    [Fact]
    public async Task MissingUnsubscribe_ReturnsCompatibleSuccess()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var scenario = await TwoUserIdentityScenarioBuilder.CreateAsync(_application.Services, cancellationToken);
        using var client = AuthenticatedTestClientFactory.Create(_application, scenario.UserA);

        using var response = await Unsubscribe(client, $"https://push.test/{Guid.NewGuid():N}", cancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task EmptyUnsubscribe_IsBadRequest()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var scenario = await TwoUserIdentityScenarioBuilder.CreateAsync(_application.Services, cancellationToken);
        using var client = AuthenticatedTestClientFactory.Create(_application, scenario.UserA);

        using var response = await Unsubscribe(client, " ", cancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Theory]
    [InlineData("subscribe")]
    [InlineData("unsubscribe")]
    public async Task AnonymousOwnershipMutation_IsUnauthorized(string action)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var input = NewSubscription();
        using var client = _application.CreateClient(new() { AllowAutoRedirect = false });

        using var response = action == "subscribe"
            ? await Subscribe(client, input, cancellationToken)
            : await Unsubscribe(client, input.Endpoint, cancellationToken);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal(0, await CountByEndpoint(input.Endpoint, cancellationToken));
    }

    [Theory]
    [InlineData("subscribe")]
    [InlineData("unsubscribe")]
    public async Task AmbiguousJwtIdentity_FailsClosed(string action)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var scenario = await TwoUserIdentityScenarioBuilder.CreateAsync(_application.Services, cancellationToken);
        var input = NewSubscription();
        var token = CreateAmbiguousIdentityToken(_application.Services, scenario.UserA.Id, scenario.UserB.Id);
        using var client = _application.CreateClient(new() { AllowAutoRedirect = false });
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        using var response = action == "subscribe"
            ? await Subscribe(client, input, cancellationToken)
            : await Unsubscribe(client, input.Endpoint, cancellationToken);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal(0, await CountByEndpoint(input.Endpoint, cancellationToken));
    }

    [Theory]
    [InlineData("", "p256dh", "auth")]
    [InlineData("https://push.test/invalid-p256dh", "", "auth")]
    [InlineData("https://push.test/invalid-auth", "p256dh", "")]
    public async Task InvalidSubscribePayload_IsBadRequest_AndDatabaseIsUnchanged(
        string endpoint,
        string p256dh,
        string auth)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var scenario = await TwoUserIdentityScenarioBuilder.CreateAsync(_application.Services, cancellationToken);
        using var client = AuthenticatedTestClientFactory.Create(_application, scenario.UserA);
        var input = new SubscriptionInput(endpoint, p256dh, auth);

        using var response = await Subscribe(client, input, cancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        if (!string.IsNullOrWhiteSpace(endpoint))
        {
            Assert.Equal(0, await CountByEndpoint(endpoint, cancellationToken));
        }
    }

    [Fact]
    public async Task ConcurrentSameSubscription_RemainsSingleAndSuccessful()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var scenario = await TwoUserIdentityScenarioBuilder.CreateAsync(_application.Services, cancellationToken);
        var input = NewSubscription();
        using var firstClient = AuthenticatedTestClientFactory.Create(_application, scenario.UserA);
        using var secondClient = AuthenticatedTestClientFactory.Create(_application, scenario.UserA);

        var responses = await Task.WhenAll(
            Subscribe(firstClient, input, cancellationToken),
            Subscribe(secondClient, input, cancellationToken));
        using var first = responses[0];
        using var second = responses[1];

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        Assert.Equal(1, await CountByEndpoint(input.Endpoint, cancellationToken));
    }

    [Fact]
    public async Task NormalUser_CannotBroadcast()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var scenario = await TwoUserIdentityScenarioBuilder.CreateAsync(_application.Services, cancellationToken);
        using var client = AuthenticatedTestClientFactory.Create(_application, scenario.UserA);
        var title = $"Forbidden-{Guid.NewGuid():N}";

        using var response = await client.PostAsJsonAsync(
            "/api/push/broadcast",
            new { title, body = "Body", url = "/events" },
            cancellationToken);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.False(await NotificationExists(title, cancellationToken));
    }

    [Fact]
    public async Task SuperAdmin_BroadcastFlow_RemainsAvailable()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var scenario = await TwoUserIdentityScenarioBuilder.CreateAsync(_application.Services, cancellationToken);
        await SetAppRole(scenario.UserA.Id, AppRole.SuperAdmin, cancellationToken);
        scenario.UserA.AppRole = AppRole.SuperAdmin;
        var input = NewSubscription();
        await SeedSubscription(input, scenario.UserB.Id, isActive: true, cancellationToken);
        using var client = AuthenticatedTestClientFactory.Create(_application, scenario.UserA);
        var title = $"Allowed-{Guid.NewGuid():N}";

        using var response = await client.PostAsJsonAsync(
            "/api/push/broadcast",
            new { title, body = "Body", url = "/events" },
            cancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(await NotificationExists(title, cancellationToken));
    }

    [Fact]
    public async Task SubscribeOpenApi_DoesNotExposeLegacyUserId()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var response = await _application.Client.GetAsync("/swagger/v1/swagger.json", cancellationToken);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
        var schemaReference = document.RootElement
            .GetProperty("paths")
            .GetProperty("/api/push/subscribe")
            .GetProperty("post")
            .GetProperty("requestBody")
            .GetProperty("content")
            .GetProperty("application/json")
            .GetProperty("schema")
            .GetProperty("$ref")
            .GetString();
        var schemaName = schemaReference?.Split('/').Last();
        var properties = document.RootElement
            .GetProperty("components")
            .GetProperty("schemas")
            .GetProperty(schemaName!)
            .GetProperty("properties");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(properties.TryGetProperty("endpoint", out _));
        Assert.True(properties.TryGetProperty("keys", out _));
        Assert.False(properties.TryGetProperty("userId", out _));
    }

    private static SubscriptionInput NewSubscription() =>
        new($"https://push.test/{Guid.NewGuid():N}", "p256dh-key", "auth-key");

    private static Task<HttpResponseMessage> Subscribe(
        HttpClient client,
        SubscriptionInput input,
        CancellationToken cancellationToken) =>
        client.PostAsJsonAsync(
            "/api/push/subscribe",
            new
            {
                endpoint = input.Endpoint,
                keys = new { p256dh = input.P256dh, auth = input.Auth },
                userAgent = "Integration Test",
                platform = "test",
                deviceName = "test-device",
            },
            cancellationToken);

    private static Task<HttpResponseMessage> Unsubscribe(
        HttpClient client,
        string endpoint,
        CancellationToken cancellationToken) =>
        client.PostAsJsonAsync("/api/push/unsubscribe", new { endpoint }, cancellationToken);

    private async Task SeedSubscription(
        SubscriptionInput input,
        Guid? userId,
        bool isActive,
        CancellationToken cancellationToken)
    {
        await using var scope = _application.Services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var now = DateTime.UtcNow;
        await context.PushSubscriptions.AddAsync(
            new PushSubscription
            {
                Endpoint = input.Endpoint,
                P256dhKey = input.P256dh,
                AuthKey = input.Auth,
                UserId = userId,
                IsActive = isActive,
                LastSeenAt = now,
                RevokedAt = isActive ? null : now,
                CreatedAt = now,
                UpdatedAt = now,
            },
            cancellationToken);
        await context.SaveChangesAsync(cancellationToken);
    }

    private async Task<SubscriptionSnapshot> LoadByEndpoint(
        string endpoint,
        CancellationToken cancellationToken)
    {
        await using var scope = _application.Services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await context.PushSubscriptions
            .AsNoTracking()
            .Where(subscription => subscription.Endpoint == endpoint.Trim())
            .Select(subscription => new SubscriptionSnapshot(
                subscription.UserId,
                subscription.P256dhKey,
                subscription.AuthKey,
                subscription.IsActive,
                subscription.RevokedAt))
            .SingleAsync(cancellationToken);
    }

    private async Task<int> CountByEndpoint(string endpoint, CancellationToken cancellationToken)
    {
        await using var scope = _application.Services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await context.PushSubscriptions
            .AsNoTracking()
            .CountAsync(subscription => subscription.Endpoint == endpoint.Trim(), cancellationToken);
    }

    private async Task SetAppRole(Guid userId, AppRole role, CancellationToken cancellationToken)
    {
        await using var scope = _application.Services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var user = await context.Users.SingleAsync(value => value.Id == userId, cancellationToken);
        user.AppRole = role;
        await context.SaveChangesAsync(cancellationToken);
    }

    private async Task<bool> NotificationExists(string title, CancellationToken cancellationToken)
    {
        await using var scope = _application.Services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await context.Notifications.AsNoTracking().AnyAsync(value => value.Title == title, cancellationToken);
    }

    private static string CreateAmbiguousIdentityToken(
        IServiceProvider services,
        Guid subjectUserId,
        Guid nameIdentifierUserId)
    {
        using var scope = services.CreateScope();
        var options = scope.ServiceProvider.GetRequiredService<IOptions<JwtOptions>>().Value;
        var credentials = new SigningCredentials(
            new SymmetricSecurityKey(Encoding.UTF8.GetBytes(options.SigningKey)),
            SecurityAlgorithms.HmacSha256);
        var token = new JwtSecurityToken(
            issuer: options.Issuer,
            audience: options.Audience,
            claims:
            [
                new Claim(JwtRegisteredClaimNames.Sub, subjectUserId.ToString()),
                new Claim("nameid", nameIdentifierUserId.ToString()),
            ],
            notBefore: DateTime.UtcNow.AddMinutes(-1),
            expires: DateTime.UtcNow.AddMinutes(5),
            signingCredentials: credentials);

        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    private sealed record SubscriptionInput(string Endpoint, string P256dh, string Auth);

    private sealed record SubscriptionSnapshot(
        Guid? UserId,
        string P256dhKey,
        string AuthKey,
        bool IsActive,
        DateTime? RevokedAt);
}
