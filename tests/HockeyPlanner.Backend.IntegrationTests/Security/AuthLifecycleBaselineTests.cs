using System.Net;
using System.Net.Http.Json;
using HockeyPlanner.Backend.Core.Entities;
using HockeyPlanner.Backend.IntegrationTests.Fixtures;
using HockeyPlanner.Backend.IntegrationTests.Infrastructure;
using HockeyPlanner.Backend.Infrastructure.Data;
using HockeyPlanner.Backend.WebAPI.Models.Auth;
using HockeyPlanner.Backend.WebAPI.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace HockeyPlanner.Backend.IntegrationTests.Security;

[Collection(IntegrationTestCollection.Name)]
[Trait("Category", "M4AuthBaseline")]
public sealed class AuthLifecycleBaselineTests
{
    private readonly HockeyPlannerWebApplicationFactory _application;

    public AuthLifecycleBaselineTests(HockeyPlannerWebApplicationFactory application)
    {
        _application = application;
    }

    [Fact]
    public async Task Refresh_RotatesToken_RecordsReplacement_AndRejectsReuse()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var scenario = await AuthLifecycleScenarioBuilder.CreateAsync(_application.Services, cancellationToken);

        using var firstResponse = await PostRefreshAsync(
            _application.Client,
            scenario.UserARefreshToken,
            cancellationToken);
        var firstBody = await firstResponse.Content.ReadFromJsonAsync<AuthResponse>(cancellationToken);

        Assert.Equal(HttpStatusCode.OK, firstResponse.StatusCode);
        Assert.NotNull(firstBody);
        Assert.NotEqual(scenario.UserARefreshToken, firstBody.RefreshToken);

        await using (var scope = _application.Services.CreateAsyncScope())
        {
            var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var tokenService = scope.ServiceProvider.GetRequiredService<IAuthTokenService>();
            var oldTokenHash = tokenService.HashToken(scenario.UserARefreshToken);
            var replacementHash = tokenService.HashToken(firstBody.RefreshToken);
            var oldToken = await dbContext.RefreshTokens
                .AsNoTracking()
                .SingleAsync(value => value.TokenHash == oldTokenHash, cancellationToken);
            var replacement = await dbContext.RefreshTokens
                .AsNoTracking()
                .SingleAsync(value => value.TokenHash == replacementHash, cancellationToken);

            Assert.NotNull(oldToken.UsedAt);
            Assert.NotNull(oldToken.RevokedAt);
            Assert.Equal(replacement.Id, oldToken.ReplacedByTokenId);
            Assert.Equal(scenario.UserA.Id, replacement.UserId);
            Assert.Null(replacement.RevokedAt);
        }

        using var replayResponse = await PostRefreshAsync(
            _application.Client,
            scenario.UserARefreshToken,
            cancellationToken);

        Assert.Equal(HttpStatusCode.Unauthorized, replayResponse.StatusCode);
    }

    [Fact]
    public async Task Logout_OwnerCanRevokeOwnRefreshToken()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var scenario = await AuthLifecycleScenarioBuilder.CreateAsync(_application.Services, cancellationToken);
        using var client = AuthenticatedTestClientFactory.Create(_application, scenario.UserA);

        using var response = await client.PostAsJsonAsync(
            "/api/auth/logout",
            new LogoutRequest { RefreshToken = scenario.UserARefreshToken },
            cancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(await IsRefreshTokenRevokedAsync(scenario.UserARefreshToken, cancellationToken));
    }

    [Fact]
    public async Task Logout_WithoutJwt_IsUnauthorized_AndTokenRemainsActive()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var scenario = await AuthLifecycleScenarioBuilder.CreateAsync(_application.Services, cancellationToken);

        using var response = await _application.Client.PostAsJsonAsync(
            "/api/auth/logout",
            new LogoutRequest { RefreshToken = scenario.UserARefreshToken },
            cancellationToken);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.False(await IsRefreshTokenRevokedAsync(scenario.UserARefreshToken, cancellationToken));
    }

    [Fact]
    public async Task PasswordReset_ConsumesPresentedToken_AndRevokesAllRefreshSessions()
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
        var tokenService = scope.ServiceProvider.GetRequiredService<IAuthTokenService>();
        var resetHash = tokenService.HashToken(scenario.PrimaryResetToken);
        var consumedReset = await dbContext.PasswordResetTokens
            .AsNoTracking()
            .SingleAsync(value => value.TokenHash == resetHash, cancellationToken);
        var refreshTokens = await dbContext.RefreshTokens
            .AsNoTracking()
            .Where(value => value.UserId == scenario.UserA.Id)
            .ToListAsync(cancellationToken);

        Assert.NotNull(consumedReset.UsedAt);
        Assert.NotEmpty(refreshTokens);
        Assert.All(refreshTokens, token => Assert.NotNull(token.RevokedAt));
    }

    [Fact]
    public async Task PasswordReset_SequentialReuseOfConsumedTokenIsRejected()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var scenario = await AuthLifecycleScenarioBuilder.CreateAsync(_application.Services, cancellationToken);
        var request = new ResetPasswordRequest
        {
            Token = scenario.PrimaryResetToken,
            NewPassword = "M4-reset-password-84",
        };

        using var firstResponse = await _application.Client.PostAsJsonAsync(
            "/api/auth/reset-password",
            request,
            cancellationToken);
        using var secondResponse = await _application.Client.PostAsJsonAsync(
            "/api/auth/reset-password",
            request,
            cancellationToken);

        Assert.Equal(HttpStatusCode.OK, firstResponse.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, secondResponse.StatusCode);
    }

    private static Task<HttpResponseMessage> PostRefreshAsync(
        HttpClient client,
        string refreshToken,
        CancellationToken cancellationToken) =>
        client.PostAsJsonAsync(
            "/api/auth/refresh",
            new RefreshTokenRequest { RefreshToken = refreshToken },
            cancellationToken);

    private async Task<bool> IsRefreshTokenRevokedAsync(
        string rawToken,
        CancellationToken cancellationToken)
    {
        await using var scope = _application.Services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var tokenService = scope.ServiceProvider.GetRequiredService<IAuthTokenService>();
        var tokenHash = tokenService.HashToken(rawToken);
        return await dbContext.RefreshTokens
            .AsNoTracking()
            .Where(value => value.TokenHash == tokenHash)
            .Select(value => value.RevokedAt != null)
            .SingleAsync(cancellationToken);
    }
}
