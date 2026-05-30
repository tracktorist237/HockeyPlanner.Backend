using HockeyPlanner.Backend.Core.Entities.Base;

namespace HockeyPlanner.Backend.Core.Entities
{
    public class ReleaseNotice : Entity
    {
        public string Version { get; set; } = string.Empty;
        public string Title { get; set; } = string.Empty;
        public string Body { get; set; } = string.Empty;
        public bool IsPublished { get; set; }
        public bool SendNotification { get; set; }
        public bool NotificationSent { get; set; }
        public DateTime? PublishedAt { get; set; }
        public Guid? CreatedByUserId { get; set; }
        public User? CreatedByUser { get; set; }
    }
}
