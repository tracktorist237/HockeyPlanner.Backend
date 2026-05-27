using HockeyPlanner.Backend.Core.Entities.Base;

namespace HockeyPlanner.Backend.Core.Entities
{
    public class NotificationPreferences : Entity
    {
        public Guid UserId { get; set; }
        public User User { get; set; } = null!;

        public bool AttendanceRequiredEnabled { get; set; } = true;
        public bool RosterReadyEnabled { get; set; } = true;
        public bool TeamNewsEnabled { get; set; } = true;
        public bool GoaliesEnabled { get; set; } = true;
        public bool BirthdaysEnabled { get; set; } = true;
        public bool AppUpdatesEnabled { get; set; } = true;
    }
}
