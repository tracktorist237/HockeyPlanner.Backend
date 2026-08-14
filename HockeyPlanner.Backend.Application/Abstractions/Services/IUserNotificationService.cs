using HockeyPlanner.Backend.Shared.Models.Notifications;

namespace HockeyPlanner.Backend.Application.Abstractions.Services;

public interface IUserNotificationService
{
    Task<NotificationsListDto> GetInbox(
        Guid actorUserId,
        int skip,
        int take,
        CancellationToken cancellationToken);

    Task MarkRead(
        Guid actorUserId,
        Guid notificationId,
        CancellationToken cancellationToken);

    Task MarkAllRead(
        Guid actorUserId,
        CancellationToken cancellationToken);

    Task<NotificationPreferencesDto> GetPreferences(
        Guid actorUserId,
        CancellationToken cancellationToken);

    Task<NotificationPreferencesDto> UpdatePreferences(
        Guid actorUserId,
        NotificationPreferencesDto request,
        CancellationToken cancellationToken);

    Task SendSelfTestNotification(
        Guid actorUserId,
        CancellationToken cancellationToken);
}
