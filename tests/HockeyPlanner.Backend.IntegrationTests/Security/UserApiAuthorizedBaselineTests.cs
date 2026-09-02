using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using HockeyPlanner.Backend.Core.Enums;
using HockeyPlanner.Backend.IntegrationTests.Fixtures;
using HockeyPlanner.Backend.IntegrationTests.Infrastructure;
using HockeyPlanner.Backend.Infrastructure.Data;
using HockeyPlanner.Backend.Shared.Models.Users;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace HockeyPlanner.Backend.IntegrationTests.Security;

[Collection(IntegrationTestCollection.Name)]
[Trait("Category", "UserApiAuthorizedBaseline")]
public sealed class UserApiAuthorizedBaselineTests
{
    private readonly HockeyPlannerWebApplicationFactory _application;

    public UserApiAuthorizedBaselineTests(HockeyPlannerWebApplicationFactory application)
    {
        _application = application;
    }

    [Fact]
    public async Task AuthenticatedUser_CanReadOwnProfile()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var scenario = await TwoUserIdentityScenarioBuilder.CreateAsync(_application.Services, cancellationToken);
        using var client = AuthenticatedTestClientFactory.Create(_application, scenario.UserA);

        using var response = await client.GetAsync($"/api/Users/{scenario.UserA.Id}", cancellationToken);
        var profile = await response.Content.ReadFromJsonAsync<UserProfileDto>(cancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.NotNull(profile);
        Assert.Equal(scenario.UserA.Id, profile.Id);
        Assert.Equal(scenario.UserA.Email, profile.Email);
        Assert.Equal(scenario.UserA.Phone, profile.Phone);
    }

    [Fact]
    public async Task SelfProfile_UsesProfileDtoContract_InsteadOfEfEntityContract()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var scenario = await TwoUserIdentityScenarioBuilder.CreateAsync(_application.Services, cancellationToken);
        using var client = AuthenticatedTestClientFactory.Create(_application, scenario.UserA);

        using var response = await client.GetAsync($"/api/Users/{scenario.UserA.Id}", cancellationToken);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(scenario.UserA.Id, document.RootElement.GetProperty("id").GetGuid());
        Assert.True(document.RootElement.TryGetProperty("fullName", out _));
        Assert.False(document.RootElement.TryGetProperty("passwordUpdatedAt", out _));
    }

    [Fact]
    public async Task AuthenticatedUser_CanReadOwnPrivacySettings()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var scenario = await TwoUserIdentityScenarioBuilder.CreateAsync(_application.Services, cancellationToken);
        using var client = AuthenticatedTestClientFactory.Create(_application, scenario.UserA);

        using var response = await client.GetAsync(
            $"/api/Users/{scenario.UserA.Id}/privacy-settings",
            cancellationToken);
        var settings = await response.Content.ReadFromJsonAsync<UserPrivacySettingsDto>(cancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.NotNull(settings);
        Assert.Equal(scenario.UserA.Id, settings.UserId);
        Assert.Equal(UserDataVisibility.Everyone, settings.EmailVisibility);
    }

    [Fact]
    public async Task AuthenticatedUser_CanUpdateOwnPrivacySettings()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var scenario = await TwoUserIdentityScenarioBuilder.CreateAsync(_application.Services, cancellationToken);
        using var client = AuthenticatedTestClientFactory.Create(_application, scenario.UserA);
        var request = new UpdateUserPrivacySettingsRequest
        {
            EmailVisibility = UserDataVisibility.Teammates,
            PhoneVisibility = UserDataVisibility.TeamAdmins,
            BirthDateVisibility = UserDataVisibility.Everyone,
            PhysicalVisibility = UserDataVisibility.Teammates,
            HockeyProfileVisibility = UserDataVisibility.Everyone,
            SpbhlProfileVisibility = UserDataVisibility.Nobody,
        };

        using var response = await client.PutAsJsonAsync(
            $"/api/Users/{scenario.UserA.Id}/privacy-settings",
            request,
            cancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        await using var scope = _application.Services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var persisted = await dbContext.UserPrivacySettings
            .AsNoTracking()
            .SingleAsync(value => value.UserId == scenario.UserA.Id, cancellationToken);
        Assert.Equal(UserDataVisibility.Teammates, persisted.EmailVisibility);
        Assert.Equal(UserDataVisibility.TeamAdmins, persisted.PhoneVisibility);
        Assert.Equal(UserDataVisibility.Everyone, persisted.BirthDateVisibility);
    }

    [Fact]
    public async Task AuthenticatedUser_CanUpdateAllowedFieldsOnOwnProfile()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var scenario = await TwoUserIdentityScenarioBuilder.CreateAsync(_application.Services, cancellationToken);
        using var client = AuthenticatedTestClientFactory.Create(_application, scenario.UserA);
        var request = new UpdateUserRequest
        {
            FirstName = "Updated",
            LastName = "Profile",
            JerseyNumber = 77,
            PrimaryPosition = (int)Position.Defender,
            Handedness = (int)Handedness.Left,
            Height = 185,
            Weight = 85,
            BirthDate = new DateTime(1992, 5, 6, 0, 0, 0, DateTimeKind.Utc),
            Phone = "+79990000000",
            PhotoUrl = "https://test.invalid/profile.png",
        };

        using var response = await client.PutAsJsonAsync(
            $"/api/Users/{scenario.UserA.Id}",
            request,
            cancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        await using var scope = _application.Services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var persisted = await dbContext.Users
            .AsNoTracking()
            .SingleAsync(value => value.Id == scenario.UserA.Id, cancellationToken);
        Assert.Equal("Updated", persisted.FirstName);
        Assert.Equal("Profile", persisted.LastName);
        Assert.Equal(77, persisted.JerseyNumber);
        Assert.Equal("+79990000000", persisted.Phone);
        Assert.Equal(scenario.UserA.PasswordHash, persisted.PasswordHash);
        Assert.Equal(AppRole.User, persisted.AppRole);
    }

    [Fact]
    public async Task UserProfileResponse_NeverContainsPasswordHash()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var scenario = await TwoUserIdentityScenarioBuilder.CreateAsync(_application.Services, cancellationToken);
        using var client = AuthenticatedTestClientFactory.Create(_application, scenario.UserA);

        using var response = await client.GetAsync($"/api/Users/{scenario.UserA.Id}", cancellationToken);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.False(document.RootElement.TryGetProperty("passwordHash", out _));
    }

    [Fact]
    public async Task AuthenticatedDirectory_UsesExactSummaryContract_AndAppliesPositionPrivacy()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var scenario = await TwoUserIdentityScenarioBuilder.CreateAsync(_application.Services, cancellationToken);
        using var client = AuthenticatedTestClientFactory.Create(_application, scenario.UserA);

        using var response = await client.GetAsync("/api/Users", cancellationToken);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var summaries = document.RootElement.EnumerateArray().ToList();
        Assert.NotEmpty(summaries);
        Assert.All(summaries, summary =>
            Assert.Equal(
                new[] { "id", "photoUrl", "primaryPosition" },
                summary.EnumerateObject().Select(property => property.Name).OrderBy(name => name).ToArray()));

        var userA = summaries.Single(summary => summary.GetProperty("id").GetGuid() == scenario.UserA.Id);
        var userB = summaries.Single(summary => summary.GetProperty("id").GetGuid() == scenario.UserB.Id);
        Assert.Equal((int)scenario.UserA.PrimaryPosition!.Value, userA.GetProperty("primaryPosition").GetInt32());
        Assert.Equal(JsonValueKind.Null, userB.GetProperty("primaryPosition").ValueKind);
    }
}
