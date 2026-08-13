using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using HockeyPlanner.Backend.Application.Abstractions.Identity;
using HockeyPlanner.Backend.Core.Entities;
using HockeyPlanner.Backend.Core.Enums;
using HockeyPlanner.Backend.IntegrationTests.Fixtures;
using HockeyPlanner.Backend.IntegrationTests.Infrastructure;
using HockeyPlanner.Backend.Infrastructure.Data;
using HockeyPlanner.Backend.Shared.Models.Users;
using HockeyPlanner.Backend.WebAPI;
using HockeyPlanner.Backend.WebAPI.Services;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace HockeyPlanner.Backend.IntegrationTests.Security;

[Collection(IntegrationTestCollection.Name)]
public sealed class UserMutationAuthorizationTests
{
    private readonly HockeyPlannerWebApplicationFactory _application;

    public UserMutationAuthorizationTests(HockeyPlannerWebApplicationFactory application)
    {
        _application = application;
    }

    [Fact]
    public async Task SelfUpdate_PersistsAllowedFields_AndPreservesServerOwnedFields()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var scenario = await TwoUserIdentityScenarioBuilder.CreateAsync(_application.Services, cancellationToken);
        var before = await LoadUser(scenario.UserA.Id, cancellationToken);
        using var client = AuthenticatedTestClientFactory.Create(_application, scenario.UserA);
        var request = new
        {
            firstName = "  Updated  First ",
            lastName = " Updated Last ",
            jerseyNumber = 74,
            primaryPosition = (int)Position.Goalie,
            handedness = (int)Handedness.Left,
            height = 188,
            weight = 88,
            phone = "+79990000000",
            passwordHash = "client-controlled-hash",
            passwordUpdatedAt = DateTime.UtcNow,
            emailConfirmed = false,
            appRole = AppRole.SuperAdmin,
            role = UserRole.Manager,
            createdAt = DateTime.UtcNow,
            updatedAt = DateTime.UtcNow,
        };

        using var response = await client.PutAsJsonAsync(
            $"/api/Users/{scenario.UserA.Id}",
            request,
            cancellationToken);
        var profile = await response.Content.ReadFromJsonAsync<UserProfileDto>(cancellationToken);
        var after = await LoadUser(scenario.UserA.Id, cancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.NotNull(profile);
        Assert.Equal("Updated First", profile.FirstName);
        Assert.Equal("Updated Last", profile.LastName);
        Assert.Equal(request.jerseyNumber, after.JerseyNumber);
        Assert.Equal(before.PasswordHash, after.PasswordHash);
        Assert.Equal(before.PasswordUpdatedAt, after.PasswordUpdatedAt);
        Assert.Equal(before.EmailConfirmed, after.EmailConfirmed);
        Assert.Equal(before.AppRole, after.AppRole);
        Assert.Equal(before.Role, after.Role);
        Assert.Equal(before.CreatedAt, after.CreatedAt);
    }

    [Fact]
    public async Task SuperAdmin_CannotUpdateAnotherUser()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var scenario = await TwoUserIdentityScenarioBuilder.CreateAsync(_application.Services, cancellationToken);
        await SetAppRole(scenario.UserA.Id, AppRole.SuperAdmin, cancellationToken);
        scenario.UserA.AppRole = AppRole.SuperAdmin;
        var before = await LoadUser(scenario.UserB.Id, cancellationToken);
        using var client = AuthenticatedTestClientFactory.Create(_application, scenario.UserA);

        using var response = await client.PutAsJsonAsync(
            $"/api/Users/{scenario.UserB.Id}",
            CreateUpdateRequest("Forbidden", "Update"),
            cancellationToken);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal(before, await LoadUser(scenario.UserB.Id, cancellationToken));
    }

    [Fact]
    public async Task UpdateMissingUser_ReturnsNotFound()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var scenario = await TwoUserIdentityScenarioBuilder.CreateAsync(_application.Services, cancellationToken);
        using var client = AuthenticatedTestClientFactory.Create(_application, scenario.UserA);

        using var response = await client.PutAsJsonAsync(
            $"/api/Users/{Guid.NewGuid()}",
            CreateUpdateRequest("Missing", "User"),
            cancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task AnonymousUpdate_IsUnauthorized()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var scenario = await TwoUserIdentityScenarioBuilder.CreateAsync(_application.Services, cancellationToken);

        using var response = await _application.Client.PutAsJsonAsync(
            $"/api/Users/{scenario.UserA.Id}",
            CreateUpdateRequest("Anonymous", "Update"),
            cancellationToken);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task MalformedCurrentIdentity_CannotUpdateUser()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var scenario = await TwoUserIdentityScenarioBuilder.CreateAsync(_application.Services, cancellationToken);
        var spy = new SpyCurrentUser(isAuthenticated: true, userId: null);
        await using var application = CreateApplication(spyCurrentUser: spy);
        using var client = CreateAuthenticatedClient(application, scenario.UserA);

        using var response = await client.PutAsJsonAsync(
            $"/api/Users/{scenario.UserA.Id}",
            CreateUpdateRequest("Malformed", "Identity"),
            cancellationToken);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.True(spy.UserIdReadCount > 0);
    }

    [Fact]
    public async Task SelfAvatarUpload_UsesStorageOnce_AndPersistsSafeResponse()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var scenario = await TwoUserIdentityScenarioBuilder.CreateAsync(_application.Services, cancellationToken);
        var storage = new SpyFileStorageService();
        await using var application = CreateApplication(storage);
        using var client = CreateAuthenticatedClient(application, scenario.UserA);
        using var content = CreateAvatarContent();

        using var response = await client.PostAsync(
            $"/api/Users/{scenario.UserA.Id}/avatar/upload",
            content,
            cancellationToken);
        var profile = await response.Content.ReadFromJsonAsync<UserProfileDto>(cancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.NotNull(profile);
        Assert.Equal(storage.PublicUrl, profile.PhotoUrl);
        Assert.Equal(1, storage.UploadCallCount);
        Assert.Equal(storage.PublicUrl, (await LoadUser(scenario.UserA.Id, cancellationToken)).PhotoUrl);
    }

    [Fact]
    public async Task ForeignAvatarUpload_IsForbiddenBeforeStorageIo()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var scenario = await TwoUserIdentityScenarioBuilder.CreateAsync(_application.Services, cancellationToken);
        var before = await LoadUser(scenario.UserB.Id, cancellationToken);
        var storage = new SpyFileStorageService();
        await using var application = CreateApplication(storage);
        using var client = CreateAuthenticatedClient(application, scenario.UserA);
        using var content = CreateAvatarContent();

        using var response = await client.PostAsync(
            $"/api/Users/{scenario.UserB.Id}/avatar/upload",
            content,
            cancellationToken);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal(0, storage.UploadCallCount);
        Assert.Equal(before, await LoadUser(scenario.UserB.Id, cancellationToken));
    }

    [Fact]
    public async Task MissingAvatarTarget_ReturnsNotFoundBeforeStorageIo()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var scenario = await TwoUserIdentityScenarioBuilder.CreateAsync(_application.Services, cancellationToken);
        var storage = new SpyFileStorageService();
        await using var application = CreateApplication(storage);
        using var client = CreateAuthenticatedClient(application, scenario.UserA);
        using var content = CreateAvatarContent();

        using var response = await client.PostAsync(
            $"/api/Users/{Guid.NewGuid()}/avatar/upload",
            content,
            cancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal(0, storage.UploadCallCount);
    }

    [Fact]
    public async Task ForeignAvatarAuthorization_DoesNotReadRequestBody()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var scenario = await TwoUserIdentityScenarioBuilder.CreateAsync(_application.Services, cancellationToken);
        var before = await LoadUser(scenario.UserB.Id, cancellationToken);
        var storage = new SpyFileStorageService();
        var bodyReads = new RequestBodyReadTracker();
        await using var application = CreateApplication(storage, bodyReads: bodyReads);
        using var client = CreateAuthenticatedClient(application, scenario.UserA);
        using var content = CreateAvatarContent();

        using var response = await client.PostAsync(
            $"/api/Users/{scenario.UserB.Id}/avatar/upload",
            content,
            cancellationToken);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal(0, bodyReads.ReadCount);
        Assert.Equal(0, storage.UploadCallCount);
        Assert.Equal(before, await LoadUser(scenario.UserB.Id, cancellationToken));
    }

    [Fact]
    public async Task MissingAvatarTarget_IsResolvedWithoutReadingRequestBody()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var scenario = await TwoUserIdentityScenarioBuilder.CreateAsync(_application.Services, cancellationToken);
        var storage = new SpyFileStorageService();
        var bodyReads = new RequestBodyReadTracker();
        await using var application = CreateApplication(storage, bodyReads: bodyReads);
        using var client = CreateAuthenticatedClient(application, scenario.UserA);
        using var content = CreateAvatarContent();

        using var response = await client.PostAsync(
            $"/api/Users/{Guid.NewGuid()}/avatar/upload",
            content,
            cancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal(0, bodyReads.ReadCount);
        Assert.Equal(0, storage.UploadCallCount);
    }

    [Fact]
    public async Task SelfAvatarRequest_ReadsFormAfterAuthorization_ThenValidatesMissingFile()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var scenario = await TwoUserIdentityScenarioBuilder.CreateAsync(_application.Services, cancellationToken);
        var before = await LoadUser(scenario.UserA.Id, cancellationToken);
        var storage = new SpyFileStorageService();
        var bodyReads = new RequestBodyReadTracker();
        await using var application = CreateApplication(storage, bodyReads: bodyReads);
        using var client = CreateAuthenticatedClient(application, scenario.UserA);
        using var content = new MultipartFormDataContent();

        using var response = await client.PostAsync(
            $"/api/Users/{scenario.UserA.Id}/avatar/upload",
            content,
            cancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.True(bodyReads.ReadCount > 0);
        Assert.Equal(0, storage.UploadCallCount);
        Assert.Equal(before, await LoadUser(scenario.UserA.Id, cancellationToken));
    }

    [Fact]
    public async Task AvatarUpload_OpenApiContract_RemainsMultipartFile()
    {
        var cancellationToken = TestContext.Current.CancellationToken;

        using var response = await _application.Client.GetAsync(
            "/swagger/v1/swagger.json",
            cancellationToken);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
        var requestBody = document.RootElement
            .GetProperty("paths")
            .GetProperty("/api/Users/{id}/avatar/upload")
            .GetProperty("post")
            .GetProperty("requestBody");
        var schema = requestBody
            .GetProperty("content")
            .GetProperty("multipart/form-data")
            .GetProperty("schema");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(requestBody.GetProperty("required").GetBoolean());
        Assert.Equal("object", schema.GetProperty("type").GetString());
        var fileSchema = schema.GetProperty("properties").GetProperty("file");
        Assert.Equal("string", fileSchema.GetProperty("type").GetString());
        Assert.Equal("binary", fileSchema.GetProperty("format").GetString());
    }

    [Fact]
    public async Task AnonymousAvatarUpload_IsUnauthorizedBeforeStorageIo()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var scenario = await TwoUserIdentityScenarioBuilder.CreateAsync(_application.Services, cancellationToken);
        var before = await LoadUser(scenario.UserA.Id, cancellationToken);
        var storage = new SpyFileStorageService();
        var bodyReads = new RequestBodyReadTracker();
        await using var application = CreateApplication(storage, bodyReads: bodyReads);
        using var client = application.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        using var content = CreateAvatarContent();

        using var response = await client.PostAsync(
            $"/api/Users/{scenario.UserA.Id}/avatar/upload",
            content,
            cancellationToken);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal(0, bodyReads.ReadCount);
        Assert.Equal(0, storage.UploadCallCount);
        Assert.Equal(before, await LoadUser(scenario.UserA.Id, cancellationToken));
    }

    [Fact]
    public async Task SuperAdmin_CannotUploadAnotherUsersAvatar()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var scenario = await TwoUserIdentityScenarioBuilder.CreateAsync(_application.Services, cancellationToken);
        await SetAppRole(scenario.UserA.Id, AppRole.SuperAdmin, cancellationToken);
        scenario.UserA.AppRole = AppRole.SuperAdmin;
        var before = await LoadUser(scenario.UserB.Id, cancellationToken);
        var storage = new SpyFileStorageService();
        var bodyReads = new RequestBodyReadTracker();
        await using var application = CreateApplication(storage, bodyReads: bodyReads);
        using var client = CreateAuthenticatedClient(application, scenario.UserA);
        using var content = CreateAvatarContent();

        using var response = await client.PostAsync(
            $"/api/Users/{scenario.UserB.Id}/avatar/upload",
            content,
            cancellationToken);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal(0, bodyReads.ReadCount);
        Assert.Equal(0, storage.UploadCallCount);
        Assert.Equal(before, await LoadUser(scenario.UserB.Id, cancellationToken));
    }

    [Fact]
    public async Task MalformedCurrentIdentity_CannotUploadAvatar()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var scenario = await TwoUserIdentityScenarioBuilder.CreateAsync(_application.Services, cancellationToken);
        var before = await LoadUser(scenario.UserA.Id, cancellationToken);
        var currentUser = new SpyCurrentUser(isAuthenticated: true, userId: null);
        var storage = new SpyFileStorageService();
        var bodyReads = new RequestBodyReadTracker();
        await using var application = CreateApplication(storage, currentUser, bodyReads);
        using var client = CreateAuthenticatedClient(application, scenario.UserA);
        using var content = CreateAvatarContent();

        using var response = await client.PostAsync(
            $"/api/Users/{scenario.UserA.Id}/avatar/upload",
            content,
            cancellationToken);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal(0, bodyReads.ReadCount);
        Assert.Equal(0, storage.UploadCallCount);
        Assert.Equal(before, await LoadUser(scenario.UserA.Id, cancellationToken));
    }

    [Fact]
    public async Task DeleteSelf_IsForbidden_AndUserRemains()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var scenario = await TwoUserIdentityScenarioBuilder.CreateAsync(_application.Services, cancellationToken);
        using var client = AuthenticatedTestClientFactory.Create(_application, scenario.UserA);

        using var response = await client.DeleteAsync($"/api/Users/{scenario.UserA.Id}", cancellationToken);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.True(await UserExists(scenario.UserA.Id, cancellationToken));
    }

    [Fact]
    public async Task SuperAdmin_CannotDeleteAnotherUser()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var scenario = await TwoUserIdentityScenarioBuilder.CreateAsync(_application.Services, cancellationToken);
        await SetAppRole(scenario.UserA.Id, AppRole.SuperAdmin, cancellationToken);
        scenario.UserA.AppRole = AppRole.SuperAdmin;
        using var client = AuthenticatedTestClientFactory.Create(_application, scenario.UserA);

        using var response = await client.DeleteAsync($"/api/Users/{scenario.UserB.Id}", cancellationToken);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.True(await UserExists(scenario.UserB.Id, cancellationToken));
    }

    [Fact]
    public async Task DeleteMissingUser_ReturnsNotFound()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var scenario = await TwoUserIdentityScenarioBuilder.CreateAsync(_application.Services, cancellationToken);
        using var client = AuthenticatedTestClientFactory.Create(_application, scenario.UserA);

        using var response = await client.DeleteAsync($"/api/Users/{Guid.NewGuid()}", cancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task AnonymousDelete_IsUnauthorized()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var scenario = await TwoUserIdentityScenarioBuilder.CreateAsync(_application.Services, cancellationToken);

        using var response = await _application.Client.DeleteAsync(
            $"/api/Users/{scenario.UserA.Id}",
            cancellationToken);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.True(await UserExists(scenario.UserA.Id, cancellationToken));
    }

    [Fact]
    public async Task MalformedCurrentIdentity_CannotDeleteUser()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var scenario = await TwoUserIdentityScenarioBuilder.CreateAsync(_application.Services, cancellationToken);
        var currentUser = new SpyCurrentUser(isAuthenticated: true, userId: null);
        await using var application = CreateApplication(spyCurrentUser: currentUser);
        using var client = CreateAuthenticatedClient(application, scenario.UserA);

        using var response = await client.DeleteAsync(
            $"/api/Users/{scenario.UserA.Id}",
            cancellationToken);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.True(await UserExists(scenario.UserA.Id, cancellationToken));
    }

    [Fact]
    public async Task AuthenticatedLegacyCreation_IsForbidden_AndCreatesNoUser()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var scenario = await TwoUserIdentityScenarioBuilder.CreateAsync(_application.Services, cancellationToken);
        using var client = AuthenticatedTestClientFactory.Create(_application, scenario.UserA);
        var marker = $"Mass-{Guid.NewGuid():N}";

        using var response = await client.PostAsJsonAsync(
            "/api/Users",
            CreateMaliciousCreatePayload(marker),
            cancellationToken);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.False(await UserExistsByFirstName(marker, cancellationToken));
    }

    [Fact]
    public async Task MalformedCurrentIdentity_CannotCreateLegacyUser()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var scenario = await TwoUserIdentityScenarioBuilder.CreateAsync(_application.Services, cancellationToken);
        var currentUser = new SpyCurrentUser(isAuthenticated: true, userId: null);
        await using var application = CreateApplication(spyCurrentUser: currentUser);
        using var client = CreateAuthenticatedClient(application, scenario.UserA);
        var marker = $"Mass-{Guid.NewGuid():N}";

        using var response = await client.PostAsJsonAsync(
            "/api/Users",
            CreateMaliciousCreatePayload(marker),
            cancellationToken);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.False(await UserExistsByFirstName(marker, cancellationToken));
    }

    [Fact]
    public async Task SuperAdmin_CannotCreateLegacyUser()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var scenario = await TwoUserIdentityScenarioBuilder.CreateAsync(_application.Services, cancellationToken);
        await SetAppRole(scenario.UserA.Id, AppRole.SuperAdmin, cancellationToken);
        scenario.UserA.AppRole = AppRole.SuperAdmin;
        using var client = AuthenticatedTestClientFactory.Create(_application, scenario.UserA);
        var marker = $"Mass-{Guid.NewGuid():N}";

        using var response = await client.PostAsJsonAsync(
            "/api/Users",
            CreateMaliciousCreatePayload(marker),
            cancellationToken);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.False(await UserExistsByFirstName(marker, cancellationToken));
    }

    private WebApplicationFactory<Program> CreateApplication(
        SpyFileStorageService? storage = null,
        SpyCurrentUser? spyCurrentUser = null,
        RequestBodyReadTracker? bodyReads = null) =>
        _application.WithWebHostBuilder(builder =>
        {
            builder.ConfigureTestServices(services =>
            {
                if (storage != null)
                {
                    services.RemoveAll<IFileStorageService>();
                    services.AddSingleton<IFileStorageService>(storage);
                }

                if (spyCurrentUser != null)
                {
                    services.RemoveAll<ICurrentUser>();
                    services.AddSingleton<ICurrentUser>(spyCurrentUser);
                }

                if (bodyReads != null)
                {
                    services.AddSingleton<IStartupFilter>(new RequestBodyReadTrackingStartupFilter(bodyReads));
                }
            });
        });

    private static HttpClient CreateAuthenticatedClient(WebApplicationFactory<Program> application, User user)
    {
        using var scope = application.Services.CreateScope();
        var tokenService = scope.ServiceProvider.GetRequiredService<IAuthTokenService>();
        var accessToken = tokenService.CreateAccessToken(user);
        var client = application.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        return client;
    }

    private static UpdateUserRequest CreateUpdateRequest(string firstName, string lastName) =>
        new()
        {
            FirstName = firstName,
            LastName = lastName,
            JerseyNumber = 74,
            PrimaryPosition = (int)Position.Goalie,
            Handedness = (int)Handedness.Left,
            Height = 188,
            Weight = 88,
            Phone = "+79990000000",
        };

    private static object CreateMaliciousCreatePayload(string firstName) =>
        new
        {
            id = Guid.NewGuid(),
            firstName,
            lastName = "Assignment",
            email = $"{firstName}@test.invalid",
            passwordHash = "client-controlled-hash",
            passwordUpdatedAt = DateTime.UtcNow,
            emailConfirmed = true,
            appRole = AppRole.SuperAdmin,
            role = UserRole.Manager,
            createdAt = DateTime.UtcNow,
            updatedAt = DateTime.UtcNow,
        };

    private static MultipartFormDataContent CreateAvatarContent()
    {
        var content = new MultipartFormDataContent();
        var image = new ByteArrayContent("test-image"u8.ToArray());
        image.Headers.ContentType = new MediaTypeHeaderValue("image/png");
        content.Add(image, "file", "avatar.png");
        return content;
    }

    private async Task<UserSnapshot> LoadUser(Guid userId, CancellationToken cancellationToken)
    {
        await using var scope = _application.Services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await context.Users
            .AsNoTracking()
            .Where(user => user.Id == userId)
            .Select(user => new UserSnapshot(
                user.Id,
                user.FirstName,
                user.LastName,
                user.JerseyNumber,
                user.PhotoUrl,
                user.PasswordHash,
                user.PasswordUpdatedAt,
                user.EmailConfirmed,
                user.AppRole,
                user.Role,
                user.CreatedAt))
            .SingleAsync(cancellationToken);
    }

    private async Task SetAppRole(Guid userId, AppRole role, CancellationToken cancellationToken)
    {
        await using var scope = _application.Services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var user = await context.Users.SingleAsync(value => value.Id == userId, cancellationToken);
        user.AppRole = role;
        await context.SaveChangesAsync(cancellationToken);
    }

    private async Task<bool> UserExists(Guid userId, CancellationToken cancellationToken)
    {
        await using var scope = _application.Services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await context.Users.AsNoTracking().AnyAsync(user => user.Id == userId, cancellationToken);
    }

    private async Task<bool> UserExistsByFirstName(string firstName, CancellationToken cancellationToken)
    {
        await using var scope = _application.Services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await context.Users.AsNoTracking().AnyAsync(user => user.FirstName == firstName, cancellationToken);
    }

    private sealed record UserSnapshot(
        Guid Id,
        string FirstName,
        string LastName,
        int? JerseyNumber,
        string? PhotoUrl,
        string? PasswordHash,
        DateTime? PasswordUpdatedAt,
        bool EmailConfirmed,
        AppRole AppRole,
        UserRole Role,
        DateTime CreatedAt);

    private sealed class RequestBodyReadTracker
    {
        private int _readCount;

        public int ReadCount => Volatile.Read(ref _readCount);

        public void RecordRead() => Interlocked.Increment(ref _readCount);
    }

    private sealed class RequestBodyReadTrackingStartupFilter : IStartupFilter
    {
        private readonly RequestBodyReadTracker _tracker;

        public RequestBodyReadTrackingStartupFilter(RequestBodyReadTracker tracker)
        {
            _tracker = tracker;
        }

        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) =>
            application =>
            {
                application.Use(async (context, continuePipeline) =>
                {
                    context.Request.Body = new TrackingReadStream(context.Request.Body, _tracker);
                    await continuePipeline();
                });
                next(application);
            };
    }

    private sealed class TrackingReadStream : Stream
    {
        private readonly Stream _inner;
        private readonly RequestBodyReadTracker _tracker;

        public TrackingReadStream(Stream inner, RequestBodyReadTracker tracker)
        {
            _inner = inner;
            _tracker = tracker;
        }

        public override bool CanRead => _inner.CanRead;
        public override bool CanSeek => _inner.CanSeek;
        public override bool CanWrite => _inner.CanWrite;
        public override long Length => _inner.Length;
        public override long Position { get => _inner.Position; set => _inner.Position = value; }

        public override void Flush() => _inner.Flush();
        public override long Seek(long offset, SeekOrigin origin) => _inner.Seek(offset, origin);
        public override void SetLength(long value) => _inner.SetLength(value);
        public override void Write(byte[] buffer, int offset, int count) => _inner.Write(buffer, offset, count);

        public override int Read(byte[] buffer, int offset, int count)
        {
            _tracker.RecordRead();
            return _inner.Read(buffer, offset, count);
        }

        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            _tracker.RecordRead();
            return await _inner.ReadAsync(buffer, cancellationToken);
        }

        public override async Task<int> ReadAsync(
            byte[] buffer,
            int offset,
            int count,
            CancellationToken cancellationToken)
        {
            _tracker.RecordRead();
            return await _inner.ReadAsync(buffer, offset, count, cancellationToken);
        }
    }
}
