using HockeyPlanner.Backend.Core.Entities;
using HockeyPlanner.Backend.Core.Enums;
using HockeyPlanner.Backend.Infrastructure.Data;
using HockeyPlanner.Backend.WebAPI.Services;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;

namespace HockeyPlanner.Backend.IntegrationTests.Fixtures;

public sealed record AuthLifecycleScenario(
    User UserA,
    User UserB,
    User CredentiallessPlayer,
    string Password,
    string UserARefreshToken,
    string UserASecondRefreshToken,
    string UserBRefreshToken,
    string PrimaryResetToken,
    string SiblingResetToken);

public static class AuthLifecycleScenarioBuilder
{
    public const string Password = "M4-baseline-password-42";

    public static async Task<AuthLifecycleScenario> CreateAsync(
        IServiceProvider services,
        CancellationToken cancellationToken = default)
    {
        var suffix = Guid.NewGuid().ToString("N");
        var now = DateTime.UtcNow;
        var userA = CreateCredentialedUser("Alice", suffix, AppRole.User);
        var userB = CreateCredentialedUser("Bob", suffix, AppRole.User);
        var credentiallessPlayer = new User
        {
            FirstName = $"Player-{suffix[..8]}",
            LastName = "Unclaimed",
            Role = UserRole.Player,
            AppRole = AppRole.User,
        };

        var passwordHasher = new PasswordHasher<User>();
        userA.PasswordHash = passwordHasher.HashPassword(userA, Password);
        userA.PasswordUpdatedAt = now.AddDays(-1);
        userB.PasswordHash = passwordHasher.HashPassword(userB, Password);
        userB.PasswordUpdatedAt = now.AddDays(-1);

        var userARefreshToken = $"refresh-a-primary-{suffix}";
        var userASecondRefreshToken = $"refresh-a-secondary-{suffix}";
        var userBRefreshToken = $"refresh-b-{suffix}";
        var primaryResetToken = $"reset-a-primary-{suffix}";
        var siblingResetToken = $"reset-a-sibling-{suffix}";

        await using var scope = services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var tokenService = scope.ServiceProvider.GetRequiredService<IAuthTokenService>();

        var records = new object[]
        {
            userA,
            userB,
            credentiallessPlayer,
            CreateRefreshToken(userA.Id, userARefreshToken, tokenService, now),
            CreateRefreshToken(userA.Id, userASecondRefreshToken, tokenService, now),
            CreateRefreshToken(userB.Id, userBRefreshToken, tokenService, now),
            CreateResetToken(userA.Id, primaryResetToken, tokenService, now),
            CreateResetToken(userA.Id, siblingResetToken, tokenService, now),
        };

        await dbContext.AddRangeAsync(records, cancellationToken);
        await dbContext.SaveChangesAsync(cancellationToken);
        dbContext.ChangeTracker.Clear();

        return new AuthLifecycleScenario(
            userA,
            userB,
            credentiallessPlayer,
            Password,
            userARefreshToken,
            userASecondRefreshToken,
            userBRefreshToken,
            primaryResetToken,
            siblingResetToken);
    }

    private static User CreateCredentialedUser(string firstName, string suffix, AppRole appRole) =>
        new()
        {
            FirstName = $"{firstName}-{suffix[..8]}",
            LastName = "AuthLifecycle",
            Email = $"{firstName.ToLowerInvariant()}-{suffix}@test.invalid",
            EmailConfirmed = true,
            Role = UserRole.Player,
            AppRole = appRole,
        };

    private static RefreshToken CreateRefreshToken(
        Guid userId,
        string rawToken,
        IAuthTokenService tokenService,
        DateTime now) =>
        new()
        {
            UserId = userId,
            TokenHash = tokenService.HashToken(rawToken),
            ExpiresAt = now.AddDays(30),
            CreatedAt = now,
            UpdatedAt = now,
        };

    private static PasswordResetToken CreateResetToken(
        Guid userId,
        string rawToken,
        IAuthTokenService tokenService,
        DateTime now) =>
        new()
        {
            UserId = userId,
            TokenHash = tokenService.HashToken(rawToken),
            ExpiresAt = now.AddMinutes(30),
            CreatedAt = now,
            UpdatedAt = now,
        };
}
