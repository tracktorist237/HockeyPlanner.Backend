using HockeyPlanner.Backend.Core.Entities.Base;
using HockeyPlanner.Backend.Core.Enums;

namespace HockeyPlanner.Backend.Core.Entities
{
    public class Notification : Entity
    {
        public Guid UserId { get; set; }
        public User User { get; set; } = null!;

        public NotificationType Type { get; set; }
        public NotificationCategory Category { get; set; }
        public string Title { get; set; } = string.Empty;
        public string Body { get; set; } = string.Empty;
        public string? Url { get; set; }
        public bool IsRead { get; set; }
        public DateTime? ReadAt { get; set; }
        public DateTime? DeliveredAt { get; set; }
    }
}
