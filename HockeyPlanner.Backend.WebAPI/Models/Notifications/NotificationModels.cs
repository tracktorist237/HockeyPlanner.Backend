using HockeyPlanner.Backend.Core.Enums;

namespace HockeyPlanner.Backend.WebAPI.Models.Notifications
{
    public class NotificationDto
    {
        public Guid Id { get; set; }
        public NotificationType Type { get; set; }
        public NotificationCategory Category { get; set; }
        public string Title { get; set; } = string.Empty;
        public string Body { get; set; } = string.Empty;
        public string? Url { get; set; }
        public bool IsRead { get; set; }
        public DateTime CreatedAt { get; set; }
        public DateTime? ReadAt { get; set; }
        public DateTime? DeliveredAt { get; set; }
    }

    public class NotificationsListDto
    {
        public IReadOnlyCollection<NotificationDto> Items { get; set; } = Array.Empty<NotificationDto>();
        public int UnreadCount { get; set; }
    }

    public class NotificationPreferencesDto
    {
        public bool AttendanceRequiredEnabled { get; set; } = true;
        public bool RosterReadyEnabled { get; set; } = true;
        public bool TeamNewsEnabled { get; set; } = true;
        public bool GoaliesEnabled { get; set; } = true;
        public bool BirthdaysEnabled { get; set; } = true;
        public bool AppUpdatesEnabled { get; set; } = true;
    }
}
