using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using HockeyPlanner.Backend.Core.Entities;
using HockeyPlanner.Backend.Infrastructure.Data;
using HockeyPlanner.Backend.IntegrationTests.Fixtures;
using HockeyPlanner.Backend.IntegrationTests.Infrastructure;
using HockeyPlanner.Backend.Shared.Models.Notifications;
using HockeyPlanner.Backend.WebAPI.Options;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

namespace HockeyPlanner.Backend.IntegrationTests.Security;

[Collection(IntegrationTestCollection.Name)]
public sealed class NotificationOwnershipSecurityTests
{
    private readonly HockeyPlannerWebApplicationFactory _application;

    public NotificationOwnershipSecurityTests(HockeyPlannerWebApplicationFactory application)
    {
        _application = application;
    }

    [Theory]
    [InlineData("GET", "/api/notifications")]
    [InlineData("POST", "/api/notifications/11111111-1111-1111-1111-111111111111/read")]
    [InlineData("POST", "/api/notifications/read-all")]
    [InlineData("GET", "/api/notifications/preferences/me")]
    [InlineData("PUT", "/api/notification-preferences/me")]
    [InlineData("POST", "/api/notifications/test")]
    public async Task AnonymousUserFacingEndpoint_IsUnauthorized(string method, string path)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var client = _application.CreateClient(new() { AllowAutoRedirect = false });
        using var request = CreateRequest(method, path);

        using var response = await client.SendAsync(request, cancellationToken);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Theory]
    [InlineData("GET", "/api/notifications")]
    [InlineData("GET", "/api/notifications/preferences/me")]
    [InlineData("POST", "/api/notifications/test")]
    public async Task AmbiguousAuthenticatedIdentity_FailsClosed(string method, string path)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var scenario = await TwoUserNotificationScenarioBuilder.CreateAsync(_application.Services, cancellationToken);
        using var client = CreateAmbiguousIdentityClient(scenario.UserA.Id, scenario.UserB.Id);
        using var request = CreateRequest(method, path);

        using var response = await client.SendAsync(request, cancellationToken);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    [Trait("Category", "SecurityExpectation")]
    public async Task Inbox_ReturnsOnlyJwtUsersNotificationsAndUnreadCount()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var scenario = await TwoUserNotificationScenarioBuilder.CreateAsync(_application.Services, cancellationToken);
        using var client = AuthenticatedTestClientFactory.Create(_application, scenario.UserA);

        using var response = await client.GetAsync("/api/notifications", cancellationToken);
        var body = await response.Content.ReadFromJsonAsync<NotificationsListDto>(cancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.NotNull(body);
        Assert.Equal(1, body.UnreadCount);
        Assert.Equal([scenario.UserAUnread.Id, scenario.UserARead.Id], body.Items.Select(item => item.Id));
        Assert.DoesNotContain(body.Items, item => item.Id == scenario.UserBUnread.Id || item.Id == scenario.UserBRead.Id);
    }

    [Fact]
    [Trait("Category", "SecurityExpectation")]
    public async Task SpoofedCurrentUserIdQuery_DoesNotChangeInboxActor()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var scenario = await TwoUserNotificationScenarioBuilder.CreateAsync(_application.Services, cancellationToken);
        using var client = AuthenticatedTestClientFactory.Create(_application, scenario.UserA);

        using var response = await client.GetAsync(
            $"/api/notifications?currentUserId={scenario.UserB.Id}",
            cancellationToken);
        var body = await response.Content.ReadFromJsonAsync<NotificationsListDto>(cancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.NotNull(body);
        Assert.All(body.Items, item => Assert.Contains(item.Id, new[] { scenario.UserAUnread.Id, scenario.UserARead.Id }));
        Assert.Equal(1, body.UnreadCount);
    }

    [Fact]
    public async Task Inbox_PreservesPaginationAndUnreadFirstOrdering()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var scenario = await TwoUserNotificationScenarioBuilder.CreateAsync(_application.Services, cancellationToken);
        using var client = AuthenticatedTestClientFactory.Create(_application, scenario.UserA);

        using var firstResponse = await client.GetAsync("/api/notifications?take=1", cancellationToken);
        using var secondResponse = await client.GetAsync("/api/notifications?skip=1&take=1", cancellationToken);
        var first = await firstResponse.Content.ReadFromJsonAsync<NotificationsListDto>(cancellationToken);
        var second = await secondResponse.Content.ReadFromJsonAsync<NotificationsListDto>(cancellationToken);

        Assert.Equal(scenario.UserAUnread.Id, Assert.Single(first!.Items).Id);
        Assert.Equal(scenario.UserARead.Id, Assert.Single(second!.Items).Id);
        Assert.Equal(1, first.UnreadCount);
        Assert.Equal(1, second.UnreadCount);
    }

    [Fact]
    public async Task OwnNotification_CanBeMarkedReadRepeatedly()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var scenario = await TwoUserNotificationScenarioBuilder.CreateAsync(_application.Services, cancellationToken);
        using var client = AuthenticatedTestClientFactory.Create(_application, scenario.UserA);

        using var first = await client.PostAsync(
            $"/api/notifications/{scenario.UserAUnread.Id}/read",
            null,
            cancellationToken);
        var afterFirst = await LoadNotification(scenario.UserAUnread.Id, cancellationToken);
        using var second = await client.PostAsync(
            $"/api/notifications/{scenario.UserAUnread.Id}/read",
            null,
            cancellationToken);
        var afterSecond = await LoadNotification(scenario.UserAUnread.Id, cancellationToken);

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        Assert.True(afterFirst.IsRead);
        Assert.NotNull(afterFirst.ReadAt);
        Assert.Equal(afterFirst, afterSecond);
    }

    [Fact]
    [Trait("Category", "SecurityExpectation")]
    public async Task ForeignNotificationRead_IsMaskedAsNotFound_AndDatabaseIsUnchanged()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var scenario = await TwoUserNotificationScenarioBuilder.CreateAsync(_application.Services, cancellationToken);
        using var client = AuthenticatedTestClientFactory.Create(_application, scenario.UserA);
        var before = await LoadNotification(scenario.UserBUnread.Id, cancellationToken);

        using var response = await client.PostAsync(
            $"/api/notifications/{scenario.UserBUnread.Id}/read?currentUserId={scenario.UserB.Id}",
            null,
            cancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal(before, await LoadNotification(scenario.UserBUnread.Id, cancellationToken));
    }

    [Fact]
    public async Task MissingNotificationRead_IsNotFound()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var scenario = await TwoUserNotificationScenarioBuilder.CreateAsync(_application.Services, cancellationToken);
        using var client = AuthenticatedTestClientFactory.Create(_application, scenario.UserA);

        using var response = await client.PostAsync(
            $"/api/notifications/{Guid.NewGuid()}/read",
            null,
            cancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    [Trait("Category", "SecurityExpectation")]
    public async Task MarkAllRead_ChangesOnlyJwtUsersUnreadRows()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var scenario = await TwoUserNotificationScenarioBuilder.CreateAsync(_application.Services, cancellationToken);
        using var client = AuthenticatedTestClientFactory.Create(_application, scenario.UserA);
        var userBBefore = await LoadNotification(scenario.UserBUnread.Id, cancellationToken);

        using var response = await client.PostAsync(
            $"/api/notifications/read-all?currentUserId={scenario.UserB.Id}",
            null,
            cancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True((await LoadNotification(scenario.UserAUnread.Id, cancellationToken)).IsRead);
        Assert.Equal(userBBefore, await LoadNotification(scenario.UserBUnread.Id, cancellationToken));
        Assert.True((await LoadNotification(scenario.UserBRead.Id, cancellationToken)).IsRead);
    }

    [Theory]
    [InlineData("/api/notifications/preferences/me")]
    [InlineData("/api/notification-preferences/me")]
    public async Task PreferencesGetAliases_ReturnJwtUsersPreferences(string path)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var scenario = await TwoUserNotificationScenarioBuilder.CreateAsync(_application.Services, cancellationToken);
        using var client = AuthenticatedTestClientFactory.Create(_application, scenario.UserA);

        using var response = await client.GetAsync(path, cancellationToken);
        var body = await response.Content.ReadFromJsonAsync<NotificationPreferencesDto>(cancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.NotNull(body);
        Assert.True(body.AttendanceRequiredEnabled);
        Assert.False(body.RosterReadyEnabled);
        Assert.False(body.AppUpdatesEnabled);
    }

    [Fact]
    [Trait("Category", "SecurityExpectation")]
    public async Task SpoofedPreferencesGet_ReturnsJwtUsersPreferences()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var scenario = await TwoUserNotificationScenarioBuilder.CreateAsync(_application.Services, cancellationToken);
        using var client = AuthenticatedTestClientFactory.Create(_application, scenario.UserA);

        using var response = await client.GetAsync(
            $"/api/notifications/preferences/me?currentUserId={scenario.UserB.Id}",
            cancellationToken);
        var body = await response.Content.ReadFromJsonAsync<NotificationPreferencesDto>(cancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(body!.AttendanceRequiredEnabled);
        Assert.False(body.RosterReadyEnabled);
    }

    [Theory]
    [InlineData("/api/notifications/preferences/me")]
    [InlineData("/api/notification-preferences/me")]
    [Trait("Category", "SecurityExpectation")]
    public async Task PreferencesPutAliases_ChangeOnlyJwtUsersPreferences(string path)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var scenario = await TwoUserNotificationScenarioBuilder.CreateAsync(_application.Services, cancellationToken);
        using var client = AuthenticatedTestClientFactory.Create(_application, scenario.UserA);
        var userBBefore = await LoadPreferences(scenario.UserB.Id, cancellationToken);
        var request = NewPreferences(false);

        using var response = await client.PutAsJsonAsync(
            $"{path}?currentUserId={scenario.UserB.Id}",
            request,
            cancellationToken);
        var body = await response.Content.ReadFromJsonAsync<NotificationPreferencesDto>(cancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.False(body!.AttendanceRequiredEnabled);
        Assert.Equal(
            new PreferencesSnapshot(false, false, false, false, false, false),
            await LoadPreferences(scenario.UserA.Id, cancellationToken));
        Assert.Equal(userBBefore, await LoadPreferences(scenario.UserB.Id, cancellationToken));
    }

    [Fact]
    public async Task MissingPreferences_AreCreatedForJwtActorOnly()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var scenario = await TwoUserNotificationScenarioBuilder.CreateAsync(_application.Services, cancellationToken);
        await DeletePreferences(scenario.UserA.Id, cancellationToken);
        using var client = AuthenticatedTestClientFactory.Create(_application, scenario.UserA);

        using var response = await client.GetAsync(
            $"/api/notifications/preferences/me?currentUserId={scenario.UserB.Id}",
            cancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(1, await CountPreferences(scenario.UserA.Id, cancellationToken));
        Assert.Equal(1, await CountPreferences(scenario.UserB.Id, cancellationToken));
        Assert.True((await LoadPreferences(scenario.UserA.Id, cancellationToken)).AppUpdatesEnabled);
    }

    [Theory]
    [InlineData("GET")]
    [InlineData("PUT")]
    public async Task MissingJwtUser_PreferencesAreNotFound_AndNotCreated(string method)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var scenario = await TwoUserNotificationScenarioBuilder.CreateAsync(_application.Services, cancellationToken);
        using var client = AuthenticatedTestClientFactory.Create(_application, scenario.UserA);
        await DeleteUser(scenario.UserA.Id, cancellationToken);
        using var request = CreateRequest(method, "/api/notifications/preferences/me");

        using var response = await client.SendAsync(request, cancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal(0, await CountPreferences(scenario.UserA.Id, cancellationToken));
    }

    [Fact]
    [Trait("Category", "SecurityExpectation")]
    public async Task SelfTestNotification_AlwaysTargetsJwtActor()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var scenario = await TwoUserNotificationScenarioBuilder.CreateAsync(_application.Services, cancellationToken);
        using var client = AuthenticatedTestClientFactory.Create(_application, scenario.UserA);
        var userABefore = await CountTestNotifications(scenario.UserA.Id, cancellationToken);
        var userBBefore = await CountTestNotifications(scenario.UserB.Id, cancellationToken);

        using var response = await client.PostAsync(
            $"/api/notifications/test?currentUserId={scenario.UserB.Id}",
            null,
            cancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(userABefore + 1, await CountTestNotifications(scenario.UserA.Id, cancellationToken));
        Assert.Equal(userBBefore, await CountTestNotifications(scenario.UserB.Id, cancellationToken));
        Assert.Equal(1, await CountDeliveriesForTestNotification(scenario.UserA.Id, cancellationToken));
    }

    [Fact]
    public async Task MissingJwtUser_SelfTestIsNotFound_WithoutSideEffects()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var scenario = await TwoUserNotificationScenarioBuilder.CreateAsync(_application.Services, cancellationToken);
        using var client = AuthenticatedTestClientFactory.Create(_application, scenario.UserA);
        await DeleteUser(scenario.UserA.Id, cancellationToken);

        using var response = await client.PostAsync("/api/notifications/test", null, cancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal(0, await CountNotifications(scenario.UserA.Id, cancellationToken));
        Assert.Equal(0, await CountDeliveries(scenario.UserA.Id, cancellationToken));
    }

    [Fact]
    public async Task MissingJwtUser_InboxAndMarkAllRemainCompatibleNoOps()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var scenario = await TwoUserNotificationScenarioBuilder.CreateAsync(_application.Services, cancellationToken);
        using var client = AuthenticatedTestClientFactory.Create(_application, scenario.UserA);
        await DeleteUser(scenario.UserA.Id, cancellationToken);

        using var inboxResponse = await client.GetAsync("/api/notifications", cancellationToken);
        var inbox = await inboxResponse.Content.ReadFromJsonAsync<NotificationsListDto>(cancellationToken);
        using var markAllResponse = await client.PostAsync("/api/notifications/read-all", null, cancellationToken);

        Assert.Equal(HttpStatusCode.OK, inboxResponse.StatusCode);
        Assert.Empty(inbox!.Items);
        Assert.Equal(0, inbox.UnreadCount);
        Assert.Equal(HttpStatusCode.OK, markAllResponse.StatusCode);
    }

    [Fact]
    public async Task NotificationOpenApi_HasNoCurrentUserIdAndPreservesDtos()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var response = await _application.Client.GetAsync("/swagger/v1/swagger.json", cancellationToken);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
        var paths = document.RootElement.GetProperty("paths");
        var operations = new[]
        {
            ("/api/notifications", "get"),
            ("/api/notifications/{id}/read", "post"),
            ("/api/notifications/read-all", "post"),
            ("/api/notifications/preferences/me", "get"),
            ("/api/notifications/preferences/me", "put"),
            ("/api/notification-preferences/me", "get"),
            ("/api/notification-preferences/me", "put"),
            ("/api/notifications/test", "post"),
        };

        foreach (var (path, method) in operations)
        {
            var operation = paths.GetProperty(path).GetProperty(method);
            if (operation.TryGetProperty("parameters", out var parameters))
            {
                Assert.DoesNotContain(
                    parameters.EnumerateArray(),
                    parameter => parameter.GetProperty("name").GetString() == "currentUserId");
            }
        }

        var schemas = document.RootElement.GetProperty("components").GetProperty("schemas");
        var notificationProperties = schemas.GetProperty("NotificationDto").GetProperty("properties");
        Assert.True(notificationProperties.TryGetProperty("id", out _));
        Assert.True(notificationProperties.TryGetProperty("deliveredAt", out _));
        Assert.False(notificationProperties.TryGetProperty("userId", out _));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    private HttpClient CreateAmbiguousIdentityClient(Guid subjectUserId, Guid nameIdentifierUserId)
    {
        var client = _application.CreateClient(new() { AllowAutoRedirect = false });
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            CreateAmbiguousIdentityToken(subjectUserId, nameIdentifierUserId));
        return client;
    }

    private string CreateAmbiguousIdentityToken(Guid subjectUserId, Guid nameIdentifierUserId)
    {
        using var scope = _application.Services.CreateScope();
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

    private static HttpRequestMessage CreateRequest(string method, string path)
    {
        var request = new HttpRequestMessage(new HttpMethod(method), path);
        if (method == "PUT")
        {
            request.Content = JsonContent.Create(NewPreferences(false));
        }

        return request;
    }

    private static NotificationPreferencesDto NewPreferences(bool value) =>
        new()
        {
            AttendanceRequiredEnabled = value,
            RosterReadyEnabled = value,
            TeamNewsEnabled = value,
            GoaliesEnabled = value,
            BirthdaysEnabled = value,
            AppUpdatesEnabled = value,
        };

    private async Task<NotificationSnapshot> LoadNotification(Guid notificationId, CancellationToken cancellationToken)
    {
        await using var scope = _application.Services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await context.Notifications
            .AsNoTracking()
            .Where(notification => notification.Id == notificationId)
            .Select(notification => new NotificationSnapshot(
                notification.IsRead,
                notification.ReadAt,
                notification.UpdatedAt))
            .SingleAsync(cancellationToken);
    }

    private async Task<PreferencesSnapshot> LoadPreferences(Guid userId, CancellationToken cancellationToken)
    {
        await using var scope = _application.Services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await context.NotificationPreferences
            .AsNoTracking()
            .Where(preferences => preferences.UserId == userId)
            .Select(preferences => new PreferencesSnapshot(
                preferences.AttendanceRequiredEnabled,
                preferences.RosterReadyEnabled,
                preferences.TeamNewsEnabled,
                preferences.GoaliesEnabled,
                preferences.BirthdaysEnabled,
                preferences.AppUpdatesEnabled))
            .SingleAsync(cancellationToken);
    }

    private async Task DeletePreferences(Guid userId, CancellationToken cancellationToken)
    {
        await using var scope = _application.Services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        await context.NotificationPreferences
            .Where(preferences => preferences.UserId == userId)
            .ExecuteDeleteAsync(cancellationToken);
    }

    private async Task DeleteUser(Guid userId, CancellationToken cancellationToken)
    {
        await using var scope = _application.Services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        await context.Users.Where(user => user.Id == userId).ExecuteDeleteAsync(cancellationToken);
    }

    private async Task<int> CountPreferences(Guid userId, CancellationToken cancellationToken)
    {
        await using var scope = _application.Services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await context.NotificationPreferences
            .AsNoTracking()
            .CountAsync(preferences => preferences.UserId == userId, cancellationToken);
    }

    private async Task<int> CountTestNotifications(Guid userId, CancellationToken cancellationToken)
    {
        await using var scope = _application.Services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await context.Notifications
            .AsNoTracking()
            .CountAsync(
                notification => notification.UserId == userId && notification.Title == "Тестовое уведомление",
                cancellationToken);
    }

    private async Task<int> CountNotifications(Guid userId, CancellationToken cancellationToken)
    {
        await using var scope = _application.Services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await context.Notifications
            .AsNoTracking()
            .CountAsync(notification => notification.UserId == userId, cancellationToken);
    }

    private async Task<int> CountDeliveries(Guid userId, CancellationToken cancellationToken)
    {
        await using var scope = _application.Services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await context.NotificationDeliveries
            .AsNoTracking()
            .CountAsync(delivery => delivery.UserId == userId, cancellationToken);
    }

    private async Task<int> CountDeliveriesForTestNotification(
        Guid userId,
        CancellationToken cancellationToken)
    {
        await using var scope = _application.Services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await context.NotificationDeliveries
            .AsNoTracking()
            .CountAsync(
                delivery =>
                    delivery.UserId == userId &&
                    delivery.Notification.Title == "Тестовое уведомление",
                cancellationToken);
    }

    private sealed record NotificationSnapshot(bool IsRead, DateTime? ReadAt, DateTime? UpdatedAt);

    private sealed record PreferencesSnapshot(
        bool AttendanceRequiredEnabled,
        bool RosterReadyEnabled,
        bool TeamNewsEnabled,
        bool GoaliesEnabled,
        bool BirthdaysEnabled,
        bool AppUpdatesEnabled);
}
