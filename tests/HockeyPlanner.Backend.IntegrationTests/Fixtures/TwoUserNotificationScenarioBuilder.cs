using HockeyPlanner.Backend.Core.Entities;
using HockeyPlanner.Backend.Core.Enums;
using HockeyPlanner.Backend.Infrastructure.Data;
using Microsoft.Extensions.DependencyInjection;

namespace HockeyPlanner.Backend.IntegrationTests.Fixtures;

public sealed record TwoUserNotificationScenario(
    User UserA,
    User UserB,
    Notification UserAUnread,
    Notification UserARead,
    Notification UserBUnread,
    Notification UserBRead,
    NotificationPreferences UserAPreferences,
    NotificationPreferences UserBPreferences);

public static class TwoUserNotificationScenarioBuilder
{
    public static async Task<TwoUserNotificationScenario> CreateAsync(
        IServiceProvider services,
        CancellationToken cancellationToken = default)
    {
        var suffix = Guid.NewGuid().ToString("N");
        var baseTime = new DateTime(2026, 8, 14, 9, 0, 0, DateTimeKind.Utc);
        var userA = CreateUser($"notify-a-{suffix}@test.invalid", $"NotifyA-{suffix[..8]}");
        var userB = CreateUser($"notify-b-{suffix}@test.invalid", $"NotifyB-{suffix[..8]}");

        var userAUnread = CreateNotification(userA.Id, $"A unread {suffix}", false, baseTime.AddMinutes(4));
        var userARead = CreateNotification(userA.Id, $"A read {suffix}", true, baseTime.AddMinutes(1));
        var userBUnread = CreateNotification(userB.Id, $"B unread {suffix}", false, baseTime.AddMinutes(3));
        var userBRead = CreateNotification(userB.Id, $"B read {suffix}", true, baseTime.AddMinutes(2));
        var userAPreferences = new NotificationPreferences
        {
            UserId = userA.Id,
            AttendanceRequiredEnabled = true,
            RosterReadyEnabled = false,
            TeamNewsEnabled = true,
            GoaliesEnabled = false,
            BirthdaysEnabled = true,
            AppUpdatesEnabled = false,
            CreatedAt = baseTime,
            UpdatedAt = baseTime,
        };
        var userBPreferences = new NotificationPreferences
        {
            UserId = userB.Id,
            AttendanceRequiredEnabled = false,
            RosterReadyEnabled = true,
            TeamNewsEnabled = false,
            GoaliesEnabled = true,
            BirthdaysEnabled = false,
            AppUpdatesEnabled = true,
            CreatedAt = baseTime,
            UpdatedAt = baseTime,
        };

        await using var scope = services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        await context.AddRangeAsync(
            new object[]
            {
                userA,
                userB,
                userAUnread,
                userARead,
                userBUnread,
                userBRead,
                userAPreferences,
                userBPreferences,
            },
            cancellationToken);
        await context.SaveChangesAsync(cancellationToken);
        context.ChangeTracker.Clear();

        return new TwoUserNotificationScenario(
            userA,
            userB,
            userAUnread,
            userARead,
            userBUnread,
            userBRead,
            userAPreferences,
            userBPreferences);
    }

    private static User CreateUser(string email, string firstName) =>
        new()
        {
            FirstName = firstName,
            LastName = "Notifications",
            Email = email,
            EmailConfirmed = true,
            PasswordHash = "not-used-by-notification-tests",
            Role = UserRole.Player,
            AppRole = AppRole.User,
        };

    private static Notification CreateNotification(
        Guid userId,
        string title,
        bool isRead,
        DateTime createdAt) =>
        new()
        {
            UserId = userId,
            Type = NotificationType.AppUpdatePublished,
            Category = NotificationCategory.AppUpdates,
            Title = title,
            Body = $"Body for {title}",
            Url = "/settings",
            IsRead = isRead,
            ReadAt = isRead ? createdAt.AddMinutes(1) : null,
            CreatedAt = createdAt,
            UpdatedAt = createdAt,
        };
}
