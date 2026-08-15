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
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using Npgsql;

namespace HockeyPlanner.Backend.IntegrationTests.Security;

[Collection(IntegrationTestCollection.Name)]
[Trait("Category", "SecurityExpectation")]
[Trait("Category", "M4SecurityExpectation")]
public sealed class AuthLifecycleSecurityExpectationTests
{
    private const string M44 = "M4.4: unsafe LinkPlayer claiming is not disabled yet.";
    private const string M46 = "M4.6: raw auth tokens and token-bearing URLs are still logged.";
    private readonly HockeyPlannerWebApplicationFactory _application;

    public AuthLifecycleSecurityExpectationTests(HockeyPlannerWebApplicationFactory application)
    {
        _application = application;
    }

    [Fact]
    public async Task ConcurrentRefresh_ConsumesOldTokenExactlyOnce()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var scenario = await AuthLifecycleScenarioBuilder.CreateAsync(_application.Services, cancellationToken);
        HashSet<Guid> initialTokenIds;
        await using (var scope = _application.Services.CreateAsyncScope())
        {
            var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            initialTokenIds = await dbContext.RefreshTokens
                .AsNoTracking()
                .Where(value => value.UserId == scenario.UserA.Id)
                .Select(value => value.Id)
                .ToHashSetAsync(cancellationToken);
        }

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

        await using var verificationScope = _application.Services.CreateAsyncScope();
        var verificationContext = verificationScope.ServiceProvider.GetRequiredService<AppDbContext>();
        var tokenService = verificationScope.ServiceProvider.GetRequiredService<IAuthTokenService>();
        var oldTokenHash = tokenService.HashToken(scenario.UserARefreshToken);
        var userTokens = await verificationContext.RefreshTokens
            .AsNoTracking()
            .Where(value => value.UserId == scenario.UserA.Id)
            .ToListAsync(cancellationToken);
        var oldToken = Assert.Single(userTokens, value => value.TokenHash == oldTokenHash);
        var replacements = userTokens
            .Where(value => !initialTokenIds.Contains(value.Id))
            .ToList();
        var replacement = Assert.Single(replacements);

        Assert.NotNull(oldToken.UsedAt);
        Assert.NotNull(oldToken.RevokedAt);
        Assert.Equal(replacement.Id, oldToken.ReplacedByTokenId);
        Assert.Null(replacement.UsedAt);
        Assert.Null(replacement.RevokedAt);
    }

    [Fact]
    public async Task UserA_CannotLogoutUserB_AndForeignRefreshTokenRemainsActive()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var scenario = await AuthLifecycleScenarioBuilder.CreateAsync(_application.Services, cancellationToken);
        using var client = AuthenticatedTestClientFactory.Create(_application, scenario.UserA);
        var before = await GetRefreshTokenAsync(scenario.UserBRefreshToken, cancellationToken);

        using var response = await client.PostAsJsonAsync(
            "/api/auth/logout",
            new LogoutRequest { RefreshToken = scenario.UserBRefreshToken },
            cancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.True(await IsRefreshTokenActiveAsync(scenario.UserBRefreshToken, cancellationToken));
        var after = await GetRefreshTokenAsync(scenario.UserBRefreshToken, cancellationToken);
        Assert.Equal(before.RevokedAt, after.RevokedAt);
        Assert.Equal(before.UsedAt, after.UsedAt);
        Assert.Equal(before.ReplacedByTokenId, after.ReplacedByTokenId);
        Assert.Equal(before.UpdatedAt, after.UpdatedAt);
    }

    [Fact]
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

    [Fact]
    public async Task IssuingNewPasswordResetToken_InvalidatesAllOlderActiveTokens()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var scenario = await AuthLifecycleScenarioBuilder.CreateAsync(_application.Services, cancellationToken);
        var userBTokenBefore = await GetPasswordResetTokenAsync(scenario.UserBResetToken, cancellationToken);
        var sender = new CaptureAuthEmailSender();
        await using var application = _application.WithWebHostBuilder(builder =>
        {
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<IAuthEmailSender>();
                services.AddSingleton<IAuthEmailSender>(sender);
            });
        });
        using var client = CreateAnonymousClient(application);

        using var response = await client.PostAsJsonAsync(
            "/api/auth/forgot-password",
            new ForgotPasswordRequest { Email = scenario.UserA.Email! },
            cancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var newestRawToken = await sender.WaitForTokenAsync(cancellationToken);
        await using var scope = _application.Services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var tokenService = scope.ServiceProvider.GetRequiredService<IAuthTokenService>();
        var now = DateTime.UtcNow;
        var userATokens = await dbContext.PasswordResetTokens
            .AsNoTracking()
            .Where(value => value.UserId == scenario.UserA.Id)
            .ToListAsync(cancellationToken);
        var userBToken = await dbContext.PasswordResetTokens
            .AsNoTracking()
            .SingleAsync(value => value.UserId == scenario.UserB.Id, cancellationToken);
        var primaryHash = tokenService.HashToken(scenario.PrimaryResetToken);
        var siblingHash = tokenService.HashToken(scenario.SiblingResetToken);
        var newestHash = tokenService.HashToken(newestRawToken);

        Assert.Equal(3, userATokens.Count);
        Assert.NotNull(Assert.Single(userATokens, token => token.TokenHash == primaryHash).UsedAt);
        Assert.NotNull(Assert.Single(userATokens, token => token.TokenHash == siblingHash).UsedAt);
        var newestToken = Assert.Single(userATokens, token => token.TokenHash == newestHash);
        Assert.Null(newestToken.UsedAt);
        Assert.True(newestToken.ExpiresAt > now);
        Assert.Equal(tokenService.HashToken(scenario.UserBResetToken), userBToken.TokenHash);
        Assert.Null(userBToken.UsedAt);
        Assert.Equal(userBTokenBefore.ExpiresAt, userBToken.ExpiresAt);
        Assert.Equal(userBTokenBefore.UpdatedAt, userBToken.UpdatedAt);

        using var invalidatedResponse = await client.PostAsJsonAsync(
            "/api/auth/reset-password",
            new ResetPasswordRequest
            {
                Token = scenario.PrimaryResetToken,
                NewPassword = "M4-invalidated-password-84",
            },
            cancellationToken);
        Assert.Equal(HttpStatusCode.BadRequest, invalidatedResponse.StatusCode);
        var unchangedUser = await dbContext.Users
            .AsNoTracking()
            .SingleAsync(value => value.Id == scenario.UserA.Id, cancellationToken);
        var unchangedRefreshTokens = await dbContext.RefreshTokens
            .AsNoTracking()
            .Where(value => value.UserId == scenario.UserA.Id)
            .ToListAsync(cancellationToken);
        Assert.Equal(
            PasswordVerificationResult.Success,
            new PasswordHasher<User>().VerifyHashedPassword(
                unchangedUser,
                unchangedUser.PasswordHash!,
                scenario.Password));
        Assert.All(unchangedRefreshTokens, token => Assert.Null(token.RevokedAt));

        using var newestResponse = await client.PostAsJsonAsync(
            "/api/auth/reset-password",
            new ResetPasswordRequest
            {
                Token = newestRawToken,
                NewPassword = "M4-newest-password-84",
            },
            cancellationToken);
        Assert.Equal(HttpStatusCode.OK, newestResponse.StatusCode);
    }

    [Fact]
    public async Task SuccessfulPasswordReset_InvalidatesEverySiblingResetToken()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var scenario = await AuthLifecycleScenarioBuilder.CreateAsync(_application.Services, cancellationToken);
        var userBResetBefore = await GetPasswordResetTokenAsync(scenario.UserBResetToken, cancellationToken);
        var userBRefreshBefore = await GetRefreshTokenAsync(scenario.UserBRefreshToken, cancellationToken);

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
        var userATokens = await dbContext.PasswordResetTokens
            .AsNoTracking()
            .Where(value => value.UserId == scenario.UserA.Id)
            .ToListAsync(cancellationToken);
        var userBResetToken = await dbContext.PasswordResetTokens
            .AsNoTracking()
            .SingleAsync(value => value.UserId == scenario.UserB.Id, cancellationToken);
        var userARefreshTokens = await dbContext.RefreshTokens
            .AsNoTracking()
            .Where(value => value.UserId == scenario.UserA.Id)
            .ToListAsync(cancellationToken);
        var userBRefreshToken = await dbContext.RefreshTokens
            .AsNoTracking()
            .SingleAsync(value => value.UserId == scenario.UserB.Id, cancellationToken);
        var persistedUser = await dbContext.Users
            .AsNoTracking()
            .SingleAsync(value => value.Id == scenario.UserA.Id, cancellationToken);

        Assert.NotEmpty(userATokens);
        Assert.All(userATokens, token => Assert.NotNull(token.UsedAt));
        Assert.NotEmpty(userARefreshTokens);
        Assert.All(userARefreshTokens, token => Assert.NotNull(token.RevokedAt));
        Assert.Null(userBResetToken.UsedAt);
        Assert.Null(userBRefreshToken.RevokedAt);
        Assert.Equal(userBResetBefore.ExpiresAt, userBResetToken.ExpiresAt);
        Assert.Equal(userBResetBefore.UpdatedAt, userBResetToken.UpdatedAt);
        Assert.Equal(userBRefreshBefore.UpdatedAt, userBRefreshToken.UpdatedAt);
        Assert.Equal(userBRefreshBefore.ReplacedByTokenId, userBRefreshToken.ReplacedByTokenId);
        Assert.Equal(
            PasswordVerificationResult.Success,
            new PasswordHasher<User>().VerifyHashedPassword(
                persistedUser,
                persistedUser.PasswordHash!,
                "M4-reset-password-84"));
    }

    [Fact]
    public async Task ConcurrentPasswordResetConsumption_SucceedsExactlyOnce()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var scenario = await AuthLifecycleScenarioBuilder.CreateAsync(_application.Services, cancellationToken);
        using var firstClient = CreateAnonymousClient(_application);
        using var secondClient = CreateAnonymousClient(_application);
        var passwords = new[]
        {
            "M4-first-password-84",
            "M4-second-password-84",
        };
        var requests = new[]
        {
            PostResetAsync(firstClient, scenario.PrimaryResetToken, passwords[0], cancellationToken),
            PostResetAsync(secondClient, scenario.PrimaryResetToken, passwords[1], cancellationToken),
        };
        var responses = await Task.WhenAll(requests);
        var successfulIndex = Array.FindIndex(
            responses,
            response => response.StatusCode == HttpStatusCode.OK);

        try
        {
            Assert.InRange(successfulIndex, 0, 1);
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

        await using var scope = _application.Services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var user = await dbContext.Users
            .AsNoTracking()
            .SingleAsync(value => value.Id == scenario.UserA.Id, cancellationToken);
        var resetTokens = await dbContext.PasswordResetTokens
            .AsNoTracking()
            .Where(value => value.UserId == scenario.UserA.Id)
            .ToListAsync(cancellationToken);
        var refreshTokens = await dbContext.RefreshTokens
            .AsNoTracking()
            .Where(value => value.UserId == scenario.UserA.Id)
            .ToListAsync(cancellationToken);

        Assert.Equal(
            PasswordVerificationResult.Success,
            new PasswordHasher<User>().VerifyHashedPassword(user, user.PasswordHash!, passwords[successfulIndex]));
        Assert.All(resetTokens, token => Assert.NotNull(token.UsedAt));
        Assert.All(refreshTokens, token => Assert.NotNull(token.RevokedAt));
        Assert.True(await IsRefreshTokenActiveAsync(scenario.UserBRefreshToken, cancellationToken));

        using var reuseResponse = await _application.Client.PostAsJsonAsync(
            "/api/auth/reset-password",
            new ResetPasswordRequest
            {
                Token = scenario.PrimaryResetToken,
                NewPassword = "M4-reused-password-84",
            },
            cancellationToken);
        Assert.Equal(HttpStatusCode.BadRequest, reuseResponse.StatusCode);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ConcurrentPasswordResetAndRefresh_LeavesNoActiveSessionAfterReset(
        bool refreshCompletesBeforeReset)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var scenario = await AuthLifecycleScenarioBuilder.CreateAsync(_application.Services, cancellationToken);
        var initialUserATokenIds = new HashSet<Guid>();
        string connectionString;
        string oldRefreshTokenHash;
        string resetTokenHash;
        RefreshToken userBRefreshBefore;
        await using (var setupScope = _application.Services.CreateAsyncScope())
        {
            var dbContext = setupScope.ServiceProvider.GetRequiredService<AppDbContext>();
            var tokenService = setupScope.ServiceProvider.GetRequiredService<IAuthTokenService>();
            connectionString = dbContext.Database.GetDbConnection().ConnectionString;
            oldRefreshTokenHash = tokenService.HashToken(scenario.UserARefreshToken);
            resetTokenHash = tokenService.HashToken(scenario.PrimaryResetToken);
            initialUserATokenIds = await dbContext.RefreshTokens
                .AsNoTracking()
                .Where(value => value.UserId == scenario.UserA.Id)
                .Select(value => value.Id)
                .ToHashSetAsync(cancellationToken);
            userBRefreshBefore = await dbContext.RefreshTokens
                .AsNoTracking()
                .SingleAsync(value => value.UserId == scenario.UserB.Id, cancellationToken);
        }

        await using var blockingConnection = new NpgsqlConnection(connectionString);
        await blockingConnection.OpenAsync(cancellationToken);
        await using var blockingTransaction = await blockingConnection.BeginTransactionAsync(cancellationToken);
        var blockingQuery = refreshCompletesBeforeReset
            ? "SELECT id FROM refresh_tokens WHERE token_hash = @tokenHash FOR UPDATE"
            : "SELECT id FROM password_reset_tokens WHERE token_hash = @tokenHash FOR UPDATE";
        var blockedTokenHash = refreshCompletesBeforeReset ? oldRefreshTokenHash : resetTokenHash;
        await using (var lockCommand = new NpgsqlCommand(
            blockingQuery,
            blockingConnection,
            blockingTransaction))
        {
            lockCommand.Parameters.AddWithValue("tokenHash", blockedTokenHash);
            Assert.NotNull(await lockCommand.ExecuteScalarAsync(cancellationToken));
        }

        using var refreshClient = CreateAnonymousClient(_application);
        using var resetClient = CreateAnonymousClient(_application);
        Task<HttpResponseMessage> refreshTask;
        Task<HttpResponseMessage> resetTask;
        if (refreshCompletesBeforeReset)
        {
            refreshTask = PostRefreshAsync(refreshClient, scenario.UserARefreshToken, cancellationToken);
            await WaitForBlockedDatabaseOperationsAsync(connectionString, 1, cancellationToken);
            resetTask = PostResetAsync(
                resetClient,
                scenario.PrimaryResetToken,
                "M4-reset-refresh-race-password-84",
                cancellationToken);
        }
        else
        {
            resetTask = PostResetAsync(
                resetClient,
                scenario.PrimaryResetToken,
                "M4-reset-refresh-race-password-84",
                cancellationToken);
            await WaitForBlockedDatabaseOperationsAsync(connectionString, 1, cancellationToken);
            refreshTask = PostRefreshAsync(refreshClient, scenario.UserARefreshToken, cancellationToken);
        }

        await WaitForBlockedDatabaseOperationsAsync(connectionString, 2, cancellationToken);
        await blockingTransaction.CommitAsync(cancellationToken);

        using var refreshResponse = await refreshTask.WaitAsync(cancellationToken);
        using var resetResponse = await resetTask.WaitAsync(cancellationToken);
        Assert.Equal(HttpStatusCode.OK, resetResponse.StatusCode);
        AuthResponse? refreshPayload = null;
        if (refreshCompletesBeforeReset)
        {
            Assert.Equal(HttpStatusCode.OK, refreshResponse.StatusCode);
            refreshPayload = await refreshResponse.Content.ReadFromJsonAsync<AuthResponse>(cancellationToken);
            Assert.NotNull(refreshPayload);
        }
        else
        {
            Assert.Equal(HttpStatusCode.Unauthorized, refreshResponse.StatusCode);
        }

        await using var verificationScope = _application.Services.CreateAsyncScope();
        var verificationContext = verificationScope.ServiceProvider.GetRequiredService<AppDbContext>();
        var persistedUser = await verificationContext.Users
            .AsNoTracking()
            .SingleAsync(value => value.Id == scenario.UserA.Id, cancellationToken);
        var userARefreshTokens = await verificationContext.RefreshTokens
            .AsNoTracking()
            .Where(value => value.UserId == scenario.UserA.Id)
            .ToListAsync(cancellationToken);
        var userBRefreshAfter = await verificationContext.RefreshTokens
            .AsNoTracking()
            .SingleAsync(value => value.UserId == scenario.UserB.Id, cancellationToken);
        var oldRefreshToken = Assert.Single(
            userARefreshTokens,
            value => value.TokenHash == oldRefreshTokenHash);
        var replacements = userARefreshTokens
            .Where(value => !initialUserATokenIds.Contains(value.Id))
            .ToList();
        if (refreshCompletesBeforeReset)
        {
            var replacement = Assert.Single(replacements);
            Assert.Equal(replacement.Id, oldRefreshToken.ReplacedByTokenId);
        }
        else
        {
            Assert.Empty(replacements);
            Assert.Null(oldRefreshToken.ReplacedByTokenId);
        }

        Assert.All(userARefreshTokens, token => Assert.NotNull(token.RevokedAt));
        Assert.Equal(userBRefreshBefore.RevokedAt, userBRefreshAfter.RevokedAt);
        Assert.Equal(userBRefreshBefore.UpdatedAt, userBRefreshAfter.UpdatedAt);
        Assert.Equal(
            PasswordVerificationResult.Success,
            new PasswordHasher<User>().VerifyHashedPassword(
                persistedUser,
                persistedUser.PasswordHash!,
                "M4-reset-refresh-race-password-84"));

        using var oldTokenReuse = await PostRefreshAsync(
            refreshClient,
            scenario.UserARefreshToken,
            cancellationToken);
        Assert.Equal(HttpStatusCode.Unauthorized, oldTokenReuse.StatusCode);
        if (refreshPayload != null)
        {
            using var replacementReuse = await PostRefreshAsync(
                refreshClient,
                refreshPayload.RefreshToken,
                cancellationToken);
            Assert.Equal(HttpStatusCode.Unauthorized, replacementReuse.StatusCode);
        }
    }

    [Fact]
    public async Task ExpiredPasswordResetToken_DoesNotMutatePasswordOrSessions()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var scenario = await AuthLifecycleScenarioBuilder.CreateAsync(_application.Services, cancellationToken);
        string originalPasswordHash;
        DateTime? originalPasswordUpdatedAt;
        await using (var setupScope = _application.Services.CreateAsyncScope())
        {
            var dbContext = setupScope.ServiceProvider.GetRequiredService<AppDbContext>();
            var tokenService = setupScope.ServiceProvider.GetRequiredService<IAuthTokenService>();
            var tokenHash = tokenService.HashToken(scenario.PrimaryResetToken);
            var token = await dbContext.PasswordResetTokens
                .SingleAsync(value => value.TokenHash == tokenHash, cancellationToken);
            token.ExpiresAt = DateTime.UtcNow.AddMinutes(-1);
            var user = await dbContext.Users
                .AsNoTracking()
                .SingleAsync(value => value.Id == scenario.UserA.Id, cancellationToken);
            originalPasswordHash = user.PasswordHash!;
            originalPasswordUpdatedAt = user.PasswordUpdatedAt;
            await dbContext.SaveChangesAsync(cancellationToken);
        }

        using var response = await _application.Client.PostAsJsonAsync(
            "/api/auth/reset-password",
            new ResetPasswordRequest
            {
                Token = scenario.PrimaryResetToken,
                NewPassword = "M4-expired-password-84",
            },
            cancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        await using var verificationScope = _application.Services.CreateAsyncScope();
        var verificationContext = verificationScope.ServiceProvider.GetRequiredService<AppDbContext>();
        var persistedUser = await verificationContext.Users
            .AsNoTracking()
            .SingleAsync(value => value.Id == scenario.UserA.Id, cancellationToken);
        var refreshTokens = await verificationContext.RefreshTokens
            .AsNoTracking()
            .Where(value => value.UserId == scenario.UserA.Id)
            .ToListAsync(cancellationToken);
        var resetTokens = await verificationContext.PasswordResetTokens
            .AsNoTracking()
            .Where(value => value.UserId == scenario.UserA.Id)
            .ToListAsync(cancellationToken);

        Assert.Equal(originalPasswordHash, persistedUser.PasswordHash);
        Assert.Equal(originalPasswordUpdatedAt, persistedUser.PasswordUpdatedAt);
        Assert.All(refreshTokens, token => Assert.Null(token.RevokedAt));
        Assert.All(resetTokens, token => Assert.Null(token.UsedAt));
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
        var token = await GetRefreshTokenAsync(rawToken, cancellationToken);
        return token.RevokedAt == null && token.ExpiresAt > DateTime.UtcNow;
    }

    private async Task<RefreshToken> GetRefreshTokenAsync(
        string rawToken,
        CancellationToken cancellationToken)
    {
        await using var scope = _application.Services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var tokenService = scope.ServiceProvider.GetRequiredService<IAuthTokenService>();
        var tokenHash = tokenService.HashToken(rawToken);
        return await dbContext.RefreshTokens
            .AsNoTracking()
            .SingleAsync(value => value.TokenHash == tokenHash, cancellationToken);
    }

    private async Task<PasswordResetToken> GetPasswordResetTokenAsync(
        string rawToken,
        CancellationToken cancellationToken)
    {
        await using var scope = _application.Services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var tokenService = scope.ServiceProvider.GetRequiredService<IAuthTokenService>();
        var tokenHash = tokenService.HashToken(rawToken);
        return await dbContext.PasswordResetTokens
            .AsNoTracking()
            .SingleAsync(value => value.TokenHash == tokenHash, cancellationToken);
    }

    private static async Task WaitForBlockedDatabaseOperationsAsync(
        string connectionString,
        int minimumCount,
        CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        await using var observerConnection = new NpgsqlConnection(connectionString);
        await observerConnection.OpenAsync(timeout.Token);

        while (true)
        {
            await using var command = new NpgsqlCommand(
                """
                SELECT count(*)
                FROM pg_stat_activity
                WHERE datname = current_database()
                  AND pid <> pg_backend_pid()
                  AND state = 'active'
                  AND wait_event_type = 'Lock'
                """,
                observerConnection);
            var blockedCount = Convert.ToInt32(await command.ExecuteScalarAsync(timeout.Token));
            if (blockedCount >= minimumCount)
            {
                return;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(20), timeout.Token);
        }
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

    private sealed class CaptureAuthEmailSender : IAuthEmailSender
    {
        private readonly TaskCompletionSource<string> _tokenSource =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task SendEmailConfirmation(User user, string token, CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public Task SendPasswordReset(User user, string token, CancellationToken cancellationToken)
        {
            _tokenSource.TrySetResult(token);
            return Task.CompletedTask;
        }

        public Task<string> WaitForTokenAsync(CancellationToken cancellationToken) =>
            _tokenSource.Task.WaitAsync(cancellationToken);
    }
}
