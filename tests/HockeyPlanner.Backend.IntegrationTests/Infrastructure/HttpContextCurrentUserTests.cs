using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using HockeyPlanner.Backend.Application.Abstractions.Identity;
using HockeyPlanner.Backend.Core.Entities;
using HockeyPlanner.Backend.WebAPI.Services.Identity;
using HockeyPlanner.Backend.WebAPI.Services;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace HockeyPlanner.Backend.IntegrationTests.Infrastructure;

[Collection(IntegrationTestCollection.Name)]
public sealed class HttpContextCurrentUserTests
{
    private readonly HockeyPlannerWebApplicationFactory _application;

    public HttpContextCurrentUserTests(HockeyPlannerWebApplicationFactory application)
    {
        _application = application;
    }

    [Fact]
    public void UnauthenticatedPrincipal_HasNoCurrentUser()
    {
        var currentUser = CreateCurrentUser([], isAuthenticated: false);

        Assert.False(currentUser.IsAuthenticated);
        Assert.Null(currentUser.UserId);
    }

    [Fact]
    public void ValidSubjectClaim_ResolvesUserId()
    {
        var userId = Guid.NewGuid();
        var currentUser = CreateCurrentUser([
            new Claim(JwtRegisteredClaimNames.Sub, userId.ToString()),
        ]);

        Assert.True(currentUser.IsAuthenticated);
        Assert.Equal(userId, currentUser.UserId);
    }

    [Fact]
    public void NameIdentifierClaim_IsUsedWhenSubjectIsAbsent()
    {
        var userId = Guid.NewGuid();
        var currentUser = CreateCurrentUser([
            new Claim(ClaimTypes.NameIdentifier, userId.ToString()),
        ]);

        Assert.Equal(userId, currentUser.UserId);
    }

    [Fact]
    public void MalformedSubject_DoesNotUseValidNameIdentifierFallback()
    {
        var currentUser = CreateCurrentUser([
            new Claim(JwtRegisteredClaimNames.Sub, "not-a-guid"),
            new Claim(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString()),
        ]);

        Assert.Null(currentUser.UserId);
    }

    [Fact]
    public void ConflictingSubjectAndNameIdentifier_HaveNoCurrentUser()
    {
        var currentUser = CreateCurrentUser([
            new Claim(JwtRegisteredClaimNames.Sub, Guid.NewGuid().ToString()),
            new Claim(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString()),
        ]);

        Assert.Null(currentUser.UserId);
    }

    [Fact]
    public void DifferentValuesForSameClaimType_HaveNoCurrentUser()
    {
        var currentUser = CreateCurrentUser([
            new Claim(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString()),
            new Claim(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString()),
        ]);

        Assert.Null(currentUser.UserId);
    }

    [Fact]
    public void IdenticalDuplicateClaims_ResolveUserId()
    {
        var userId = Guid.NewGuid();
        var currentUser = CreateCurrentUser([
            new Claim(ClaimTypes.NameIdentifier, userId.ToString()),
            new Claim(ClaimTypes.NameIdentifier, userId.ToString()),
        ]);

        Assert.Equal(userId, currentUser.UserId);
    }

    [Fact]
    public async Task RealJwt_PrincipalClaims_MatchCurrentJwtBearerMapping()
    {
        await using var scope = _application.Services.CreateAsyncScope();
        var tokenService = scope.ServiceProvider.GetRequiredService<IAuthTokenService>();
        var user = new User
        {
            FirstName = "Jwt",
            LastName = "Claims",
            Email = $"jwt-claims-{Guid.NewGuid():N}@example.test",
        };
        var token = tokenService.CreateAccessToken(user);
        var httpContext = new DefaultHttpContext
        {
            RequestServices = scope.ServiceProvider,
        };
        httpContext.Request.Headers.Authorization = $"Bearer {token}";

        var result = await httpContext.AuthenticateAsync(JwtBearerDefaults.AuthenticationScheme);

        Assert.True(result.Succeeded);
        Assert.NotNull(result.Principal);
        var rawSubjectClaims = result.Principal.FindAll(JwtRegisteredClaimNames.Sub).ToList();
        var nameIdentifierClaims = result.Principal.FindAll(ClaimTypes.NameIdentifier).ToList();
        var expectedClaimTypes = new[]
        {
            ClaimTypes.Email,
            ClaimTypes.Name,
            ClaimTypes.NameIdentifier,
            ClaimTypes.NameIdentifier,
            ClaimTypes.Role,
            "app_role",
            "app_role_id",
            "aud",
            "exp",
            "hockey_role_id",
            "iss",
        }.OrderBy(value => value, StringComparer.Ordinal).ToList();
        var actualClaimTypes = result.Principal.Claims
            .Select(claim => claim.Type)
            .OrderBy(value => value, StringComparer.Ordinal)
            .ToList();
        var actualClaims = string.Join(
            Environment.NewLine,
            result.Principal.Claims.Select(claim => $"{claim.Type}={claim.Value}"));
        TestContext.Current.TestOutputHelper?.WriteLine(actualClaims);
        Assert.Equal(expectedClaimTypes, actualClaimTypes);
        Assert.True(
            rawSubjectClaims.Count == 0 &&
            nameIdentifierClaims.Count == 2 &&
            nameIdentifierClaims.All(claim => claim.Value == user.Id.ToString()),
            $"Unexpected authenticated principal claims:{Environment.NewLine}{actualClaims}");

        ICurrentUser currentUser = CreateCurrentUser(result.Principal.Claims);
        Assert.True(currentUser.IsAuthenticated);
        Assert.Equal(user.Id, currentUser.UserId);
    }

    private static HttpContextCurrentUser CreateCurrentUser(
        IEnumerable<Claim> claims,
        bool isAuthenticated = true)
    {
        var identity = new ClaimsIdentity(claims, isAuthenticated ? "Test" : null);
        var httpContext = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(identity),
        };
        var accessor = new HttpContextAccessor
        {
            HttpContext = httpContext,
        };

        return new HttpContextCurrentUser(accessor);
    }
}
