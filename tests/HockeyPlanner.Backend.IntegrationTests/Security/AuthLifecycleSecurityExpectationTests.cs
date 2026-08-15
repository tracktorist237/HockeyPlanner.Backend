using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text;
using HockeyPlanner.Backend.Core.Entities;
using HockeyPlanner.Backend.IntegrationTests.Fixtures;
using HockeyPlanner.Backend.IntegrationTests.Infrastructure;
using HockeyPlanner.Backend.Infrastructure.Data;
using HockeyPlanner.Backend.WebAPI;
using HockeyPlanner.Backend.WebAPI.Models.Auth;
using HockeyPlanner.Backend.WebAPI.Options;
using HockeyPlanner.Backend.WebAPI.Services;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

namespace HockeyPlanner.Backend.IntegrationTests.Security;

[Collection(IntegrationTestCollection.Name)]
[Trait("Category", "SecurityExpectation")]
[Trait("Category", "M4SecurityExpectation")]
public sealed class AuthLifecycleSecurityExpectationTests
{
    private const string M42 = "M4.2: atomic refresh rotation and owner-bound logout are not implemented yet.";
    private const string M43 = "M4.3: complete reset-token invalidation is not implemented yet.";
    private const string M44 = "M4.4: unsafe LinkPlayer claiming is not disabled yet.";
    private const string M46 = "M4.6: raw auth tokens and token-bearing URLs are still logged.";
    private readonly HockeyPlannerWebApplicationFactory _application;

    public AuthLifecycleSecurityExpectationTests(HockeyPlannerWebApplicationFactory application)
    {
        _application = application;
    }

    [Fact(Skip = M42)]
    public async Task ConcurrentRefresh_ConsumesOldTokenExactlyOnce()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var scenario = await AuthLifecycleScenarioBuilder.CreateAsync(_application.Services, cancellationToken);
        using var firstClient = CreateAnonymousClient(_application);
        using var secondClient = CreateAnonymousClient(_application);

        var requests = new[]
        {
            PostRefreshAsync(firstClient, scenario.UserARefreshToken, cancellationToken),
            PostRefreshAsync(secondClient, scenario.UserARefreshToken, cancellationToken),
        };
        var responses = await Task.WhenAll(requests);

        try
        {
            Assert.Single(responses, response => response.StatusCode == HttpStatusCode.OK);
            Assert.Single(responses, response => response.StatusCode == HttpStatusCode.Unauthorized);
        }
        finally
        {
            foreach (var response in responses)
            {
                response.Dispose();
            }
        }
    }

    [Fact(Skip = M42)]
    public async Task UserA_CannotLogoutUserB_AndForeignRefreshTokenRemainsActive()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var scenario = await AuthLifecycleScenarioBuilder.CreateAsync(_application.Services, cancellationToken);
        using var client = AuthenticatedTestClientFactory.Create(_application, scenario.UserA);

        using var response = await client.PostAsJsonAsync(
            "/api/auth/logout",
            new LogoutRequest { RefreshToken = scenario.UserBRefreshToken },
            cancellationToken);

        Assert.True(
            response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.NotFound,
            $"Expected a denied or masked foreign logout, got {(int)response.StatusCode}.");
        Assert.True(await IsRefreshTokenActiveAsync(scenario.UserBRefreshToken, cancellationToken));
    }

    [Fact(Skip = M42)]
    public async Task AmbiguousAuthenticatedIdentity_CannotLogoutAnyRefreshToken()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var scenario = await AuthLifecycleScenarioBuilder.CreateAsync(_application.Services, cancellationToken);
        var token = CreateAmbiguousIdentityToken(
            _application.Services,
            scenario.UserA.Id,
            scenario.UserB.Id);
        using var client = CreateAnonymousClient(_application);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        using var response = await client.PostAsJsonAsync(
            "/api/auth/logout",
            new LogoutRequest { RefreshToken = scenario.UserBRefreshToken },
            cancellationToken);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.True(await IsRefreshTokenActiveAsync(scenario.UserBRefreshToken, cancellationToken));
    }

    [Fact(Skip = M43)]
    public async Task IssuingNewPasswordResetToken_InvalidatesAllOlderActiveTokens()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var scenario = await AuthLifecycleScenarioBuilder.CreateAsync(_application.Services, cancellationToken);

        using var response = await _application.Client.PostAsJsonAsync(
            "/api/auth/forgot-password",
            new ForgotPasswordRequest { Email = scenario.UserA.Email! },
            cancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        await using var scope = _application.Services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var now = DateTime.UtcNow;
        var tokens = await dbContext.PasswordResetTokens
            .AsNoTracking()
            .Where(value => value.UserId == scenario.UserA.Id)
            .ToListAsync(cancellationToken);

        Assert.Equal(3, tokens.Count);
        Assert.Single(tokens, token => token.UsedAt == null && token.ExpiresAt > now);
    }

    [Fact(Skip = M43)]
    public async Task SuccessfulPasswordReset_InvalidatesEverySiblingResetToken()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var scenario = await AuthLifecycleScenarioBuilder.CreateAsync(_application.Services, cancellationToken);

        using var response = await _application.Client.PostAsJsonAsync(
            "/api/auth/reset-password",
            new ResetPasswordRequest
            {
                Token = scenario.PrimaryResetToken,
                NewPassword = "M4-reset-password-84",
            },
            cancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        await using var scope = _application.Services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var now = DateTime.UtcNow;
        var tokens = await dbContext.PasswordResetTokens
            .AsNoTracking()
            .Where(value => value.UserId == scenario.UserA.Id)
            .ToListAsync(cancellationToken);

        Assert.NotEmpty(tokens);
        Assert.All(tokens, token => Assert.True(token.UsedAt != null || token.ExpiresAt <= now));
    }

    [Fact(Skip = M43)]
    public async Task ConcurrentPasswordResetConsumption_SucceedsExactlyOnce()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var scenario = await AuthLifecycleScenarioBuilder.CreateAsync(_application.Services, cancellationToken);
        using var firstClient = CreateAnonymousClient(_application);
        using var secondClient = CreateAnonymousClient(_application);
        var requests = new[]
        {
            PostResetAsync(firstClient, scenario.PrimaryResetToken, "M4-first-password-84", cancellationToken),
            PostResetAsync(secondClient, scenario.PrimaryResetToken, "M4-second-password-84", cancellationToken),
        };
        var responses = await Task.WhenAll(requests);

        try
        {
            Assert.Single(responses, response => response.StatusCode == HttpStatusCode.OK);
            Assert.Single(responses, response => response.StatusCode == HttpStatusCode.BadRequest);
        }
        finally
        {
            foreach (var response in responses)
            {
                response.Dispose();
            }
        }
    }

    [Fact(Skip = M44)]
    public async Task AuthenticatedUser_CannotClaimArbitraryCredentiallessPlayer_AndDatabaseIsUnchanged()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var scenario = await AuthLifecycleScenarioBuilder.CreateAsync(_application.Services, cancellationToken);
        using var client = AuthenticatedTestClientFactory.Create(_application, scenario.UserA);

        using var response = await client.PostAsJsonAsync(
            "/api/auth/link-player",
            new LinkPlayerRequest { UserId = scenario.CredentiallessPlayer.Id },
            cancellationToken);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        await using var scope = _application.Services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var persistedUsers = await dbContext.Users
            .AsNoTracking()
            .Where(value => value.Id == scenario.UserA.Id || value.Id == scenario.CredentiallessPlayer.Id)
            .OrderBy(value => value.Id)
            .ToListAsync(cancellationToken);

        Assert.Equal(2, persistedUsers.Count);
        var persistedActor = Assert.Single(persistedUsers, value => value.Id == scenario.UserA.Id);
        var persistedPlayer = Assert.Single(persistedUsers, value => value.Id == scenario.CredentiallessPlayer.Id);
        Assert.Equal(scenario.UserA.Email, persistedActor.Email);
        Assert.Equal(scenario.UserA.PasswordHash, persistedActor.PasswordHash);
        Assert.Null(persistedPlayer.Email);
        Assert.Null(persistedPlayer.PasswordHash);
    }

    [Fact(Skip = M46)]
    public async Task LoggingAuthEmailSender_DoesNotLogRawConfirmationOrResetTokens()
    {
        using var provider = new CapturedLoggerProvider();
        using var loggerFactory = LoggerFactory.Create(builder => builder.AddProvider(provider));
        var sender = new LoggingAuthEmailSender(loggerFactory.CreateLogger<LoggingAuthEmailSender>());
        var user = new User
        {
            FirstName = "Log",
            LastName = "Policy",
            Email = "log-policy@test.invalid",
        };
        const string confirmationToken = "raw-confirmation-token-m4";
        const string resetToken = "raw-reset-token-m4";

        await sender.SendEmailConfirmation(user, confirmationToken, TestContext.Current.CancellationToken);
        await sender.SendPasswordReset(user, resetToken, TestContext.Current.CancellationToken);

        Assert.DoesNotContain(provider.Messages, message => message.Contains(confirmationToken, StringComparison.Ordinal));
        Assert.DoesNotContain(provider.Messages, message => message.Contains(resetToken, StringComparison.Ordinal));
    }

    [Theory(Skip = M46)]
    [InlineData(false, "resetToken=")]
    [InlineData(true, "token=")]
    public async Task QueuedAuthEmailTimeout_DoesNotLogTokenBearingUrl(
        bool confirmation,
        string forbiddenQueryName)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var scenario = await AuthLifecycleScenarioBuilder.CreateAsync(_application.Services, cancellationToken);
        if (confirmation)
        {
            await SetEmailConfirmedAsync(scenario.UserA.Id, false, cancellationToken);
        }

        using var loggerProvider = new CapturedLoggerProvider();
        var sender = new ThrowingCaptureAuthEmailSender();
        await using var application = _application.WithWebHostBuilder(builder =>
        {
            builder.ConfigureLogging(logging => logging.AddProvider(loggerProvider));
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<IAuthEmailSender>();
                services.AddSingleton<IAuthEmailSender>(sender);
            });
        });

        using var client = confirmation
            ? CreateAuthenticatedClient(application, scenario.UserA)
            : CreateAnonymousClient(application);
        using var response = confirmation
            ? await client.PostAsync("/api/auth/resend-email-confirmation", null, cancellationToken)
            : await client.PostAsJsonAsync(
                "/api/auth/forgot-password",
                new ForgotPasswordRequest { Email = scenario.UserA.Email! },
                cancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var rawToken = await sender.WaitForTokenAsync(cancellationToken);
        var messages = await loggerProvider.WaitForAsync(
            values => values.Any(value => value.Contains("SMTP timeout", StringComparison.Ordinal)),
            cancellationToken);

        Assert.DoesNotContain(messages, message => message.Contains(rawToken, StringComparison.Ordinal));
        Assert.DoesNotContain(messages, message =>
            message.Contains(forbiddenQueryName, StringComparison.OrdinalIgnoreCase));
    }

    private static Task<HttpResponseMessage> PostRefreshAsync(
        HttpClient client,
        string refreshToken,
        CancellationToken cancellationToken) =>
        client.PostAsJsonAsync(
            "/api/auth/refresh",
            new RefreshTokenRequest { RefreshToken = refreshToken },
            cancellationToken);

    private static Task<HttpResponseMessage> PostResetAsync(
        HttpClient client,
        string resetToken,
        string password,
        CancellationToken cancellationToken) =>
        client.PostAsJsonAsync(
            "/api/auth/reset-password",
            new ResetPasswordRequest { Token = resetToken, NewPassword = password },
            cancellationToken);

    private async Task<bool> IsRefreshTokenActiveAsync(
        string rawToken,
        CancellationToken cancellationToken)
    {
        await using var scope = _application.Services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var tokenService = scope.ServiceProvider.GetRequiredService<IAuthTokenService>();
        var tokenHash = tokenService.HashToken(rawToken);
        var token = await dbContext.RefreshTokens
            .AsNoTracking()
            .SingleAsync(value => value.TokenHash == tokenHash, cancellationToken);
        return token.RevokedAt == null && token.ExpiresAt > DateTime.UtcNow;
    }

    private async Task SetEmailConfirmedAsync(
        Guid userId,
        bool emailConfirmed,
        CancellationToken cancellationToken)
    {
        await using var scope = _application.Services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var user = await dbContext.Users.SingleAsync(value => value.Id == userId, cancellationToken);
        user.EmailConfirmed = emailConfirmed;
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    private static HttpClient CreateAnonymousClient(WebApplicationFactory<Program> application) =>
        application.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

    private static HttpClient CreateAuthenticatedClient(
        WebApplicationFactory<Program> application,
        User user)
    {
        using var scope = application.Services.CreateScope();
        var tokenService = scope.ServiceProvider.GetRequiredService<IAuthTokenService>();
        var client = CreateAnonymousClient(application);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            tokenService.CreateAccessToken(user));
        return client;
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

    private sealed class ThrowingCaptureAuthEmailSender : IAuthEmailSender
    {
        private readonly TaskCompletionSource<string> _tokenSource =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task SendEmailConfirmation(User user, string token, CancellationToken cancellationToken) =>
            CaptureAndThrow(token);

        public Task SendPasswordReset(User user, string token, CancellationToken cancellationToken) =>
            CaptureAndThrow(token);

        public Task<string> WaitForTokenAsync(CancellationToken cancellationToken) =>
            _tokenSource.Task.WaitAsync(cancellationToken);

        private Task CaptureAndThrow(string token)
        {
            _tokenSource.TrySetResult(token);
            throw new TimeoutException($"Expected timeout while handling credential {token}.");
        }
    }
}
