using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text;
using HockeyPlanner.Backend.Application.Abstractions.Identity;
using HockeyPlanner.Backend.Core.Entities;
using HockeyPlanner.Backend.Core.Enums;
using HockeyPlanner.Backend.IntegrationTests.Fixtures;
using HockeyPlanner.Backend.IntegrationTests.Infrastructure;
using HockeyPlanner.Backend.Infrastructure.Data;
using HockeyPlanner.Backend.Shared.Models.Users;
using HockeyPlanner.Backend.WebAPI;
using HockeyPlanner.Backend.WebAPI.Options;
using HockeyPlanner.Backend.WebAPI.Services;
using HockeyPlanner.Backend.WebAPI.Services.Identity;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

namespace HockeyPlanner.Backend.IntegrationTests.Security;

[Collection(IntegrationTestCollection.Name)]
public sealed class UserReadPrivacyAuthorizationTests
{
    private const string BirthdayTimeZoneId = "Europe/Moscow";
    private static readonly DateTimeOffset FixedBirthdayInstant =
        new(2032, 5, 20, 12, 0, 0, TimeSpan.Zero);

    private readonly HockeyPlannerWebApplicationFactory _application;

    public UserReadPrivacyAuthorizationTests(HockeyPlannerWebApplicationFactory application)
    {
        _application = application;
    }

    [Fact]
    public async Task AnonymousPublicProfile_IgnoresSpoofedViewerIdentity()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var scenario = await TwoUserIdentityScenarioBuilder.CreateAsync(_application.Services, cancellationToken);

        using var response = await _application.Client.GetAsync(
            $"/api/Users/{scenario.UserA.Id}?currentUserId={scenario.UserA.Id}",
            cancellationToken);
        var profile = await response.Content.ReadFromJsonAsync<UserProfileDto>(cancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.NotNull(profile);
        Assert.Equal(scenario.UserA.Email, profile.Email);
        Assert.Null(profile.Phone);
        Assert.Null(profile.BirthDate);
        Assert.Null(profile.PrimaryPosition);
    }

    [Fact]
    public async Task Teammate_CanSeeFieldsConfiguredForTeammates()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var scenario = await TwoUserIdentityScenarioBuilder.CreateAsync(_application.Services, cancellationToken);
        var teamId = await CreateTeamContext(
            _application.Services,
            scenario,
            TeamMemberRole.Member,
            UserDataVisibility.Teammates,
            cancellationToken);
        using var client = AuthenticatedTestClientFactory.Create(_application, scenario.UserA);

        var profile = await client.GetFromJsonAsync<UserProfileDto>(
            $"/api/Users/{scenario.UserB.Id}?teamId={teamId}",
            cancellationToken);

        Assert.NotNull(profile);
        Assert.Equal(scenario.UserB.Email, profile.Email);
        Assert.Equal(scenario.UserB.Phone, profile.Phone);
        Assert.Equal(scenario.UserB.BirthDate, profile.BirthDate);
        Assert.Equal(scenario.UserB.PrimaryPosition, profile.PrimaryPosition);
        Assert.Equal(scenario.UserB.Height, profile.Height);
    }

    [Fact]
    public async Task TeamAdmin_CanSeeFieldsConfiguredForTeamAdmins()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var scenario = await TwoUserIdentityScenarioBuilder.CreateAsync(_application.Services, cancellationToken);
        var teamId = await CreateTeamContext(
            _application.Services,
            scenario,
            TeamMemberRole.Admin,
            UserDataVisibility.TeamAdmins,
            cancellationToken);
        using var client = AuthenticatedTestClientFactory.Create(_application, scenario.UserA);

        var profile = await client.GetFromJsonAsync<UserProfileDto>(
            $"/api/Users/{scenario.UserB.Id}?teamId={teamId}",
            cancellationToken);

        Assert.NotNull(profile);
        Assert.Equal(scenario.UserB.Phone, profile.Phone);
        Assert.Equal(scenario.UserB.Height, profile.Height);
        Assert.Equal(scenario.UserB.PrimaryPosition, profile.PrimaryPosition);
    }

    [Fact]
    public async Task SuperAdmin_CanSeePrivacyProtectedProfileFields()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var scenario = await TwoUserIdentityScenarioBuilder.CreateAsync(_application.Services, cancellationToken);
        await SetAppRole(scenario.UserA.Id, AppRole.SuperAdmin, cancellationToken);
        scenario.UserA.AppRole = AppRole.SuperAdmin;
        using var client = AuthenticatedTestClientFactory.Create(_application, scenario.UserA);

        var profile = await client.GetFromJsonAsync<UserProfileDto>(
            $"/api/Users/{scenario.UserB.Id}",
            cancellationToken);

        Assert.NotNull(profile);
        Assert.Equal(scenario.UserB.Email, profile.Email);
        Assert.Equal(scenario.UserB.Phone, profile.Phone);
        Assert.Equal(scenario.UserB.BirthDate, profile.BirthDate);
        Assert.Equal(scenario.UserB.Height, profile.Height);
    }

    [Fact]
    public async Task SpoofedTeamId_DoesNotCreateTeammateOrAdminVisibility()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var scenario = await TwoUserIdentityScenarioBuilder.CreateAsync(_application.Services, cancellationToken);
        var teamId = await CreateTargetOnlyTeamContext(
            scenario,
            UserDataVisibility.Teammates,
            cancellationToken);
        using var client = AuthenticatedTestClientFactory.Create(_application, scenario.UserA);

        var profile = await client.GetFromJsonAsync<UserProfileDto>(
            $"/api/Users/{scenario.UserB.Id}?teamId={teamId}",
            cancellationToken);

        Assert.NotNull(profile);
        Assert.Null(profile.Email);
        Assert.Null(profile.Phone);
        Assert.Null(profile.BirthDate);
        Assert.Null(profile.PrimaryPosition);
        Assert.Null(profile.Height);
    }

    [Fact]
    public async Task Birthday_IsVisibleToTeammate_WhenBirthDateVisibilityAllowsIt()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var timeProvider = new FixedTimeProvider(FixedBirthdayInstant);
        await using var application = CreateApplicationWithTimeProvider(timeProvider);
        var scenario = await TwoUserIdentityScenarioBuilder.CreateAsync(application.Services, cancellationToken);
        await CreateTeamContext(
            application.Services,
            scenario,
            TeamMemberRole.Member,
            UserDataVisibility.Teammates,
            cancellationToken,
            birthdayInstant: timeProvider.GetUtcNow());
        using var client = CreateAuthenticatedClient(application, scenario.UserA);

        var result = await client.GetFromJsonAsync<BirthdaysTodayResponse>(
            $"/api/Users/birthdays/today?currentUserId={scenario.UserB.Id}",
            cancellationToken);

        Assert.NotNull(result);
        Assert.Contains(result.Users, user => user.UserId == scenario.UserB.Id);
    }

    [Fact]
    public async Task HiddenBirthday_DoesNotRevealBirthdayOrAgeToTeammate()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var timeProvider = new FixedTimeProvider(FixedBirthdayInstant);
        await using var application = CreateApplicationWithTimeProvider(timeProvider);
        var scenario = await TwoUserIdentityScenarioBuilder.CreateAsync(application.Services, cancellationToken);
        await CreateTeamContext(
            application.Services,
            scenario,
            TeamMemberRole.Member,
            UserDataVisibility.Nobody,
            cancellationToken,
            birthdayInstant: timeProvider.GetUtcNow());
        using var client = CreateAuthenticatedClient(application, scenario.UserA);

        var result = await client.GetFromJsonAsync<BirthdaysTodayResponse>(
            "/api/Users/birthdays/today",
            cancellationToken);

        Assert.NotNull(result);
        Assert.DoesNotContain(result.Users, user => user.UserId == scenario.UserB.Id);
    }

    [Fact]
    public async Task AnonymousCannotReadBirthdays()
    {
        using var response = await _application.Client.GetAsync(
            "/api/Users/birthdays/today",
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task AnonymousCannotReadPrivacySettings()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var scenario = await TwoUserIdentityScenarioBuilder.CreateAsync(_application.Services, cancellationToken);

        using var response = await _application.Client.GetAsync(
            $"/api/Users/{scenario.UserA.Id}/privacy-settings",
            cancellationToken);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task ForeignPrivacyUpdate_IsForbidden_AndSettingsRemainUnchanged()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var scenario = await TwoUserIdentityScenarioBuilder.CreateAsync(_application.Services, cancellationToken);
        using var client = AuthenticatedTestClientFactory.Create(_application, scenario.UserA);
        var request = CreatePrivacyRequest(UserDataVisibility.Everyone);

        using var response = await client.PutAsJsonAsync(
            $"/api/Users/{scenario.UserB.Id}/privacy-settings?currentUserId={scenario.UserB.Id}",
            request,
            cancellationToken);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        await using var scope = _application.Services.CreateAsyncScope();
        var persisted = await scope.ServiceProvider.GetRequiredService<AppDbContext>()
            .UserPrivacySettings
            .AsNoTracking()
            .SingleAsync(settings => settings.UserId == scenario.UserB.Id, cancellationToken);
        Assert.Equal(UserDataVisibility.Nobody, persisted.EmailVisibility);
        Assert.Equal(UserDataVisibility.Nobody, persisted.HockeyProfileVisibility);
    }

    [Fact]
    public async Task MissingPrivacyTarget_ReturnsNotFound()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var scenario = await TwoUserIdentityScenarioBuilder.CreateAsync(_application.Services, cancellationToken);
        using var client = AuthenticatedTestClientFactory.Create(_application, scenario.UserA);

        using var response = await client.GetAsync(
            $"/api/Users/{Guid.NewGuid()}/privacy-settings",
            cancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Theory]
    [InlineData("/api/Users")]
    [InlineData("/api/Users/birthdays/today")]
    public async Task MalformedAuthenticatedIdentity_FailsClosedOnProtectedReads(string route)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var scenario = await TwoUserIdentityScenarioBuilder.CreateAsync(_application.Services, cancellationToken);
        var spy = new SpyCurrentUser(isAuthenticated: true, userId: null);
        await using var application = CreateApplicationWithCurrentUser(spy);
        using var client = CreateAuthenticatedClient(application, scenario.UserA);

        using var response = await client.GetAsync(route, cancellationToken);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.True(spy.UserIdReadCount > 0);
    }

    [Fact]
    public async Task MalformedAuthenticatedIdentity_FailsClosedOnOptionalProfileRead()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var scenario = await TwoUserIdentityScenarioBuilder.CreateAsync(_application.Services, cancellationToken);
        var spy = new SpyCurrentUser(isAuthenticated: true, userId: null);
        await using var application = CreateApplicationWithCurrentUser(spy);
        using var client = CreateAuthenticatedClient(application, scenario.UserA);

        using var response = await client.GetAsync($"/api/Users/{scenario.UserA.Id}", cancellationToken);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.True(spy.IsAuthenticatedReadCount > 0);
        Assert.True(spy.UserIdReadCount > 0);
    }

    [Fact]
    public async Task MalformedAuthenticatedIdentity_FailsClosedOnPrivacyRead()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var scenario = await TwoUserIdentityScenarioBuilder.CreateAsync(_application.Services, cancellationToken);
        var spy = new SpyCurrentUser(isAuthenticated: true, userId: null);
        await using var application = CreateApplicationWithCurrentUser(spy);
        using var client = CreateAuthenticatedClient(application, scenario.UserA);

        using var response = await client.GetAsync(
            $"/api/Users/{scenario.UserA.Id}/privacy-settings",
            cancellationToken);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.True(spy.UserIdReadCount > 0);
    }

    [Fact]
    public async Task MalformedAuthenticatedIdentity_CannotUpdatePrivacySettings()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var scenario = await TwoUserIdentityScenarioBuilder.CreateAsync(_application.Services, cancellationToken);
        var spy = new SpyCurrentUser(isAuthenticated: true, userId: null);
        await using var application = CreateApplicationWithCurrentUser(spy);
        using var client = CreateAuthenticatedClient(application, scenario.UserA);

        using var response = await client.PutAsJsonAsync(
            $"/api/Users/{scenario.UserA.Id}/privacy-settings",
            CreatePrivacyRequest(UserDataVisibility.Nobody),
            cancellationToken);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        await using var scope = _application.Services.CreateAsyncScope();
        var persisted = await scope.ServiceProvider.GetRequiredService<AppDbContext>()
            .UserPrivacySettings
            .AsNoTracking()
            .SingleAsync(settings => settings.UserId == scenario.UserA.Id, cancellationToken);
        Assert.Equal(scenario.UserAPrivacy.EmailVisibility, persisted.EmailVisibility);
    }

    [Fact]
    public async Task AmbiguousIdentityClaims_AuthenticateButFailClosedThroughRealJwtPipeline()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var scenario = await TwoUserIdentityScenarioBuilder.CreateAsync(_application.Services, cancellationToken);
        var token = CreateAmbiguousIdentityToken(
            _application.Services,
            scenario.UserA.Id,
            scenario.UserB.Id);
        var tokenClaims = new JwtSecurityTokenHandler().ReadJwtToken(token).Claims.ToList();

        Assert.Contains(tokenClaims, claim =>
            claim.Type == JwtRegisteredClaimNames.Sub && claim.Value == scenario.UserA.Id.ToString());
        Assert.Contains(tokenClaims, claim =>
            claim.Type == "nameid" && claim.Value == scenario.UserB.Id.ToString());

        await using var scope = _application.Services.CreateAsyncScope();
        var httpContext = new DefaultHttpContext
        {
            RequestServices = scope.ServiceProvider,
        };
        httpContext.Request.Headers.Authorization = $"Bearer {token}";

        var authentication = await httpContext.AuthenticateAsync(
            JwtBearerDefaults.AuthenticationScheme);

        Assert.True(authentication.Succeeded, authentication.Failure?.ToString());
        Assert.NotNull(authentication.Principal);
        var mappedIdentityValues = authentication.Principal
            .FindAll(ClaimTypes.NameIdentifier)
            .Select(claim => claim.Value)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(value => value, StringComparer.Ordinal)
            .ToArray();
        Assert.Equal(
            new[] { scenario.UserA.Id.ToString(), scenario.UserB.Id.ToString() }
                .OrderBy(value => value, StringComparer.Ordinal)
                .ToArray(),
            mappedIdentityValues);

        httpContext.User = authentication.Principal;
        var currentUser = new HttpContextCurrentUser(new HttpContextAccessor
        {
            HttpContext = httpContext,
        });
        Assert.True(currentUser.IsAuthenticated);
        Assert.Null(currentUser.UserId);

        using var client = _application.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
        });
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await client.GetAsync("/api/Users", cancellationToken);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    private async Task<Guid> CreateTeamContext(
        IServiceProvider services,
        TwoUserIdentityScenario scenario,
        TeamMemberRole viewerRole,
        UserDataVisibility visibility,
        CancellationToken cancellationToken,
        DateTimeOffset? birthdayInstant = null)
    {
        var team = CreateTeam(scenario.UserB.Id);
        var teamId = team.Id;
        var viewerMembership = CreateMembership(teamId, scenario.UserA.Id, viewerRole);
        var targetMembership = CreateMembership(teamId, scenario.UserB.Id, TeamMemberRole.Owner);

        await using var scope = services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var settings = await context.UserPrivacySettings
            .SingleAsync(value => value.UserId == scenario.UserB.Id, cancellationToken);
        SetAllVisibility(settings, visibility);
        if (birthdayInstant.HasValue)
        {
            var target = await context.Users.SingleAsync(user => user.Id == scenario.UserB.Id, cancellationToken);
            target.BirthDate = BirthdayTodayUtc(birthdayInstant.Value);
        }

        await context.AddRangeAsync(
            new object[] { team, viewerMembership, targetMembership },
            cancellationToken);
        await context.SaveChangesAsync(cancellationToken);
        return teamId;
    }

    private async Task<Guid> CreateTargetOnlyTeamContext(
        TwoUserIdentityScenario scenario,
        UserDataVisibility visibility,
        CancellationToken cancellationToken)
    {
        var team = CreateTeam(scenario.UserB.Id);
        var teamId = team.Id;
        var targetMembership = CreateMembership(teamId, scenario.UserB.Id, TeamMemberRole.Owner);

        await using var scope = _application.Services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var settings = await context.UserPrivacySettings
            .SingleAsync(value => value.UserId == scenario.UserB.Id, cancellationToken);
        SetAllVisibility(settings, visibility);
        await context.AddRangeAsync(new object[] { team, targetMembership }, cancellationToken);
        await context.SaveChangesAsync(cancellationToken);
        return teamId;
    }

    private async Task SetAppRole(Guid userId, AppRole role, CancellationToken cancellationToken)
    {
        await using var scope = _application.Services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var user = await context.Users.SingleAsync(value => value.Id == userId, cancellationToken);
        user.AppRole = role;
        await context.SaveChangesAsync(cancellationToken);
    }

    private WebApplicationFactory<Program> CreateApplicationWithCurrentUser(SpyCurrentUser spy) =>
        _application.WithWebHostBuilder(builder =>
        {
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<ICurrentUser>();
                services.AddSingleton<ICurrentUser>(spy);
            });
        });

    private WebApplicationFactory<Program> CreateApplicationWithTimeProvider(TimeProvider timeProvider) =>
        _application.WithWebHostBuilder(builder =>
        {
            builder.ConfigureAppConfiguration((_, configuration) =>
            {
                configuration.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["BIRTHDAY_TIMEZONE"] = BirthdayTimeZoneId,
                });
            });
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<TimeProvider>();
                services.AddSingleton(timeProvider);
            });
        });

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

    private static Team CreateTeam(Guid creatorUserId) =>
        new()
        {
            Name = $"Privacy {Guid.NewGuid():N}",
            InviteCode = $"P{Guid.NewGuid():N}"[..12],
            Visibility = TeamVisibility.Private,
            CreatedByUserId = creatorUserId,
        };

    private static TeamMembership CreateMembership(
        Guid teamId,
        Guid userId,
        TeamMemberRole role) =>
        new()
        {
            TeamId = teamId,
            UserId = userId,
            Role = role,
        };

    private static void SetAllVisibility(
        UserPrivacySettings settings,
        UserDataVisibility visibility)
    {
        settings.EmailVisibility = visibility;
        settings.PhoneVisibility = visibility;
        settings.BirthDateVisibility = visibility;
        settings.PhysicalVisibility = visibility;
        settings.HockeyProfileVisibility = visibility;
        settings.SpbhlProfileVisibility = visibility;
    }

    private static UpdateUserPrivacySettingsRequest CreatePrivacyRequest(
        UserDataVisibility visibility) =>
        new()
        {
            EmailVisibility = visibility,
            PhoneVisibility = visibility,
            BirthDateVisibility = visibility,
            PhysicalVisibility = visibility,
            HockeyProfileVisibility = visibility,
            SpbhlProfileVisibility = visibility,
        };

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

    private static DateTime BirthdayTodayUtc(DateTimeOffset instant)
    {
        var timeZone = TimeZoneInfo.FindSystemTimeZoneById(BirthdayTimeZoneId);
        var today = TimeZoneInfo.ConvertTime(instant, timeZone).Date;
        return new DateTime(1991, today.Month, today.Day, 0, 0, 0, DateTimeKind.Utc);
    }
}
