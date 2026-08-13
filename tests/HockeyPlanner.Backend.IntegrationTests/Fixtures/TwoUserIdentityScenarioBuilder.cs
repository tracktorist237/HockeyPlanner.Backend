using HockeyPlanner.Backend.Core.Entities;
using HockeyPlanner.Backend.Core.Enums;
using HockeyPlanner.Backend.Infrastructure.Data;
using Microsoft.Extensions.DependencyInjection;

namespace HockeyPlanner.Backend.IntegrationTests.Fixtures;

public sealed record TwoUserIdentityScenario(
    User UserA,
    User UserB,
    UserPrivacySettings UserAPrivacy,
    UserPrivacySettings UserBPrivacy);

public static class TwoUserIdentityScenarioBuilder
{
    public static async Task<TwoUserIdentityScenario> CreateAsync(
        IServiceProvider services,
        CancellationToken cancellationToken = default)
    {
        var suffix = Guid.NewGuid().ToString("N");
        var passwordUpdatedAt = DateTime.UtcNow.AddDays(-1);

        var userA = new User
        {
            FirstName = $"Alice-{suffix[..8]}",
            LastName = "Identity",
            Email = $"alice-{suffix}@test.invalid",
            EmailConfirmed = true,
            Phone = "+70000000001",
            PasswordHash = $"hash-a-{suffix}",
            PasswordUpdatedAt = passwordUpdatedAt,
            Role = UserRole.Player,
            AppRole = AppRole.User,
            JerseyNumber = 11,
            PrimaryPosition = Position.Forward,
            Height = 180,
            Weight = 80,
            BirthDate = new DateTime(1990, 1, 2, 0, 0, 0, DateTimeKind.Utc),
        };
        var userB = new User
        {
            FirstName = $"Bob-{suffix[..8]}",
            LastName = "Identity",
            Email = $"bob-{suffix}@test.invalid",
            EmailConfirmed = true,
            Phone = "+70000000002",
            PasswordHash = $"hash-b-{suffix}",
            PasswordUpdatedAt = passwordUpdatedAt,
            Role = UserRole.Player,
            AppRole = AppRole.User,
            JerseyNumber = 22,
            PrimaryPosition = Position.Defender,
            Height = 190,
            Weight = 90,
            BirthDate = new DateTime(1991, 3, 4, 0, 0, 0, DateTimeKind.Utc),
        };

        var userAPrivacy = new UserPrivacySettings
        {
            UserId = userA.Id,
            EmailVisibility = UserDataVisibility.Everyone,
            PhoneVisibility = UserDataVisibility.Teammates,
            BirthDateVisibility = UserDataVisibility.Teammates,
            PhysicalVisibility = UserDataVisibility.TeamAdmins,
            HockeyProfileVisibility = UserDataVisibility.Teammates,
            SpbhlProfileVisibility = UserDataVisibility.Everyone,
        };
        var userBPrivacy = CreatePrivateSettings(userB.Id);

        await using var scope = services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        await dbContext.AddRangeAsync(
            new object[] { userA, userB, userAPrivacy, userBPrivacy },
            cancellationToken);
        await dbContext.SaveChangesAsync(cancellationToken);
        dbContext.ChangeTracker.Clear();

        return new TwoUserIdentityScenario(userA, userB, userAPrivacy, userBPrivacy);
    }

    private static UserPrivacySettings CreatePrivateSettings(Guid userId) =>
        new()
        {
            UserId = userId,
            EmailVisibility = UserDataVisibility.Nobody,
            PhoneVisibility = UserDataVisibility.Nobody,
            BirthDateVisibility = UserDataVisibility.Nobody,
            PhysicalVisibility = UserDataVisibility.Nobody,
            HockeyProfileVisibility = UserDataVisibility.Nobody,
            SpbhlProfileVisibility = UserDataVisibility.Nobody,
        };
}
