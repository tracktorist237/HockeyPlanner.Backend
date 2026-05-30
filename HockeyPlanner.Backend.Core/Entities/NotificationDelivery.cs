using HockeyPlanner.Backend.Core.Entities.Base;
using HockeyPlanner.Backend.Core.Enums;

namespace HockeyPlanner.Backend.Core.Entities
{
    public class NotificationDelivery : Entity
    {
        public Guid NotificationId { get; set; }
        public Notification Notification { get; set; } = null!;

        public Guid UserId { get; set; }
        public User User { get; set; } = null!;

        public Guid? PushSubscriptionId { get; set; }
        public PushSubscription? PushSubscription { get; set; }

        public NotificationDeliveryStatus Status { get; set; } = NotificationDeliveryStatus.Pending;
        public string? Error { get; set; }
        public string? EndpointHash { get; set; }
        public DateTime? SentAt { get; set; }
    }
}
