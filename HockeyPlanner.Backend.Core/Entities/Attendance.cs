using HockeyPlanner.Backend.Core.Entities.Base;
using HockeyPlanner.Backend.Core.Enums;

namespace HockeyPlanner.Backend.Core.Entities
{
    public class Attendance : Entity
    {
        public Guid EventId { get; set; }
        public ScheduledEvent Event { get; set; } = null!;

        public Guid UserId { get; set; }
        public User User { get; set; } = null!;

        public AttendanceStatus Status { get; set; }
        public DateTime RespondedAt { get; set; } = DateTime.UtcNow;
        public string? Notes { get; set; }
    }
}
