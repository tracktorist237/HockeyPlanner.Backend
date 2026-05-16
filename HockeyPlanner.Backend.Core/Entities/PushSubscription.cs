using HockeyPlanner.Backend.Core.Entities.Base;

namespace HockeyPlanner.Backend.Core.Entities
{
    public class PushSubscription : Entity
    {
        public string Endpoint { get; set; } = string.Empty;
        public string P256dhKey { get; set; } = string.Empty;
        public string AuthKey { get; set; } = string.Empty;
        public Guid? UserId { get; set; }
        public User? User { get; set; }
        public string? UserAgent { get; set; }
        public DateTime? LastBirthdayNotificationAt { get; set; }
    }
}

