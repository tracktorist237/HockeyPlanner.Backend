using HockeyPlanner.Backend.Core.Enums;

namespace HockeyPlanner.Backend.Application.Abstractions.Services
{
    public interface INotificationService
    {
        Task NotifyUserAsync(
            Guid userId,
            NotificationType type,
            NotificationCategory category,
            string title,
            string body,
            string? url = null,
            CancellationToken cancellationToken = default);

        Task NotifyUsersAsync(
            IReadOnlyCollection<Guid> userIds,
            NotificationType type,
            NotificationCategory category,
            string title,
            string body,
            string? url = null,
            CancellationToken cancellationToken = default);

        Task NotifyTeamAsync(
            Guid teamId,
            NotificationType type,
            NotificationCategory category,
            string title,
            string body,
            string? url = null,
            CancellationToken cancellationToken = default);
    }
}
