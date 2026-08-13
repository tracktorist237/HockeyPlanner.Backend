using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using HockeyPlanner.Backend.Core.Enums;
using HockeyPlanner.Backend.IntegrationTests.Fixtures;
using HockeyPlanner.Backend.IntegrationTests.Infrastructure;
using HockeyPlanner.Backend.Infrastructure.Data;
using HockeyPlanner.Backend.Shared.Models.Users;
using HockeyPlanner.Backend.WebAPI.Models.Users;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace HockeyPlanner.Backend.IntegrationTests.Security;

[Collection(IntegrationTestCollection.Name)]
public sealed class UserApiSecurityExpectationTests
{
    private const string M3ActivationReason = "Activate after the corresponding M3 user API security fix";
    private readonly HockeyPlannerWebApplicationFactory _application;

    public UserApiSecurityExpectationTests(HockeyPlannerWebApplicationFactory application)
    {
        _application = application;
    }

    [Fact]
    [Trait("Category", "SecurityExpectation")]
    public async Task AnonymousUserDirectory_IsUnauthorized()
    {
        using var response = await _application.Client.GetAsync(
            "/api/Users",
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    [Trait("Category", "SecurityExpectation")]
    public async Task AuthenticatedUserDirectory_NeverContainsPasswordHash()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var scenario = await TwoUserIdentityScenarioBuilder.CreateAsync(_application.Services, cancellationToken);
        using var client = AuthenticatedTestClientFactory.Create(_application, scenario.UserA);

        using var response = await client.GetAsync("/api/Users", cancellationToken);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.All(document.RootElement.EnumerateArray(), user =>
            Assert.False(user.TryGetProperty("passwordHash", out _)));
    }

    [Fact]
    [Trait("Category", "SecurityExpectation")]
    public async Task AuthenticatedUserDirectory_ContainsNoAuthOrSystemOwnedMetadata()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var scenario = await TwoUserIdentityScenarioBuilder.CreateAsync(_application.Services, cancellationToken);
        using var client = AuthenticatedTestClientFactory.Create(_application, scenario.UserA);

        using var response = await client.GetAsync("/api/Users", cancellationToken);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
        var forbiddenProperties = new[]
        {
            "email",
            "phone",
            "emailConfirmed",
            "passwordHash",
            "passwordUpdatedAt",
            "appRole",
        };

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.All(document.RootElement.EnumerateArray(), user =>
            Assert.All(forbiddenProperties, property =>
                Assert.False(user.TryGetProperty(property, out _), $"Directory exposed '{property}'.")));
    }

    [Fact(Skip = M3ActivationReason)]
    [Trait("Category", "SecurityExpectation")]
    public async Task UserA_CannotUpdateUserB_AndUserBRemainsUnchanged()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var scenario = await TwoUserIdentityScenarioBuilder.CreateAsync(_application.Services, cancellationToken);
        using var client = AuthenticatedTestClientFactory.Create(_application, scenario.UserA);
        var request = new UpdateUserRequest
        {
            FirstName = "Unauthorized",
            LastName = "Update",
            JerseyNumber = 99,
            PrimaryPosition = (int)Position.Goalie,
        };

        using var response = await client.PutAsJsonAsync(
            $"/api/Users/{scenario.UserB.Id}",
            request,
            cancellationToken);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        var persisted = await LoadUserSnapshot(scenario.UserB.Id, cancellationToken);
        Assert.Equal(scenario.UserB.FirstName, persisted.FirstName);
        Assert.Equal(scenario.UserB.LastName, persisted.LastName);
        Assert.Equal(scenario.UserB.JerseyNumber, persisted.JerseyNumber);
    }

    [Fact(Skip = M3ActivationReason)]
    [Trait("Category", "SecurityExpectation")]
    public async Task UserA_CannotDeleteUserB_AndUserBRemainsInDatabase()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var scenario = await TwoUserIdentityScenarioBuilder.CreateAsync(_application.Services, cancellationToken);
        using var client = AuthenticatedTestClientFactory.Create(_application, scenario.UserA);

        using var response = await client.DeleteAsync($"/api/Users/{scenario.UserB.Id}", cancellationToken);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.True(await UserExists(scenario.UserB.Id, cancellationToken));
    }

    [Fact(Skip = M3ActivationReason)]
    [Trait("Category", "SecurityExpectation")]
    public async Task UserA_CannotUploadUserBAvatar_AndPhotoRemainsUnchanged()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var scenario = await TwoUserIdentityScenarioBuilder.CreateAsync(_application.Services, cancellationToken);
        using var client = AuthenticatedTestClientFactory.Create(_application, scenario.UserA);
        using var content = new MultipartFormDataContent();
        using var image = new ByteArrayContent(Encoding.UTF8.GetBytes("not-a-real-image"));
        image.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("image/png");
        content.Add(image, "file", "avatar.png");

        using var response = await client.PostAsync(
            $"/api/Users/{scenario.UserB.Id}/avatar/upload",
            content,
            cancellationToken);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        var persisted = await LoadUserSnapshot(scenario.UserB.Id, cancellationToken);
        Assert.Equal(scenario.UserB.PhotoUrl, persisted.PhotoUrl);
    }

    [Fact]
    [Trait("Category", "SecurityExpectation")]
    public async Task SpoofedCurrentUserId_DoesNotReplaceJwtViewer_WhenReadingOwnProfile()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var scenario = await TwoUserIdentityScenarioBuilder.CreateAsync(_application.Services, cancellationToken);
        using var client = AuthenticatedTestClientFactory.Create(_application, scenario.UserA);

        using var response = await client.GetAsync(
            $"/api/Users/{scenario.UserA.Id}?currentUserId={scenario.UserB.Id}",
            cancellationToken);
        var profile = await response.Content.ReadFromJsonAsync<UserProfileDto>(cancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.NotNull(profile);
        Assert.Equal(scenario.UserA.Email, profile.Email);
        Assert.Equal(scenario.UserA.Phone, profile.Phone);
    }

    [Fact]
    [Trait("Category", "SecurityExpectation")]
    public async Task SpoofedCurrentUserId_DoesNotRevealPrivacyProtectedFields()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var scenario = await TwoUserIdentityScenarioBuilder.CreateAsync(_application.Services, cancellationToken);
        using var client = AuthenticatedTestClientFactory.Create(_application, scenario.UserA);

        using var response = await client.GetAsync(
            $"/api/Users/{scenario.UserB.Id}?currentUserId={scenario.UserB.Id}",
            cancellationToken);
        var profile = await response.Content.ReadFromJsonAsync<UserProfileDto>(cancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.NotNull(profile);
        Assert.Null(profile.Email);
        Assert.Null(profile.Phone);
        Assert.Null(profile.BirthDate);
        Assert.Null(profile.Height);
        Assert.Null(profile.Weight);
    }

    [Fact]
    [Trait("Category", "SecurityExpectation")]
    public async Task SpoofedCurrentUserId_DoesNotGrantForeignPrivacySettingsAccess()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var scenario = await TwoUserIdentityScenarioBuilder.CreateAsync(_application.Services, cancellationToken);
        using var client = AuthenticatedTestClientFactory.Create(_application, scenario.UserA);

        using var response = await client.GetAsync(
            $"/api/Users/{scenario.UserB.Id}/privacy-settings?currentUserId={scenario.UserB.Id}",
            cancellationToken);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact(Skip = M3ActivationReason)]
    [Trait("Category", "SecurityExpectation")]
    public async Task LegacyUserCreation_CannotAssignServerOwnedAuthenticationFields()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var suffix = Guid.NewGuid().ToString("N");
        var firstName = $"Mass-{suffix[..8]}";
        const string lastName = "Assignment";
        var request = new
        {
            firstName,
            lastName,
            email = $"mass-{suffix}@test.invalid",
            passwordHash = "client-controlled-hash",
            emailConfirmed = true,
            passwordUpdatedAt = DateTime.UtcNow,
            appRole = AppRole.SuperAdmin,
            role = UserRole.Player,
            primaryPosition = Position.Forward,
            handedness = Handedness.Right,
        };

        using var response = await _application.Client.PostAsJsonAsync(
            "/api/Users",
            request,
            cancellationToken);

        if (response.IsSuccessStatusCode)
        {
            await using var scope = _application.Services.CreateAsyncScope();
            var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var created = await dbContext.Users
                .AsNoTracking()
                .SingleAsync(
                    value => value.FirstName == firstName && value.LastName == lastName,
                    cancellationToken);
            Assert.Null(created.PasswordHash);
            Assert.False(created.EmailConfirmed);
            Assert.Null(created.PasswordUpdatedAt);
            Assert.Equal(AppRole.User, created.AppRole);
            return;
        }

        Assert.Contains(
            response.StatusCode,
            new[] { HttpStatusCode.BadRequest, HttpStatusCode.Unauthorized, HttpStatusCode.Forbidden, HttpStatusCode.UnprocessableEntity });
        Assert.False(await UserExistsByName(firstName, lastName, cancellationToken));
    }

    private async Task<UserSnapshot> LoadUserSnapshot(Guid userId, CancellationToken cancellationToken)
    {
        await using var scope = _application.Services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await dbContext.Users
            .AsNoTracking()
            .Where(value => value.Id == userId)
            .Select(value => new UserSnapshot(
                value.FirstName,
                value.LastName,
                value.JerseyNumber,
                value.PhotoUrl))
            .SingleAsync(cancellationToken);
    }

    private async Task<bool> UserExists(Guid userId, CancellationToken cancellationToken)
    {
        await using var scope = _application.Services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await dbContext.Users.AsNoTracking().AnyAsync(value => value.Id == userId, cancellationToken);
    }

    private async Task<bool> UserExistsByName(
        string firstName,
        string lastName,
        CancellationToken cancellationToken)
    {
        await using var scope = _application.Services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await dbContext.Users.AsNoTracking().AnyAsync(
            value => value.FirstName == firstName && value.LastName == lastName,
            cancellationToken);
    }

    private sealed record UserSnapshot(
        string FirstName,
        string LastName,
        int? JerseyNumber,
        string? PhotoUrl);
}
