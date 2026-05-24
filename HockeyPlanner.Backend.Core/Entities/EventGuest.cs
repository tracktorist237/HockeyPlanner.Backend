using HockeyPlanner.Backend.Core.Entities.Base;
using HockeyPlanner.Backend.Core.Enums;

namespace HockeyPlanner.Backend.Core.Entities
{
    public class EventGuest : Entity
    {
        public Guid EventId { get; set; }
        public ScheduledEvent Event { get; set; } = null!;

        public Guid InvitedByUserId { get; set; }
        public User InvitedByUser { get; set; } = null!;

        public string FirstName { get; set; } = string.Empty;
        public string LastName { get; set; } = string.Empty;
        public Handedness? Handedness { get; set; }
        public int? JerseyNumber { get; set; }
        public AttendanceStatus Status { get; set; } = AttendanceStatus.Confirmed;
        public DateTime RespondedAt { get; set; } = DateTime.UtcNow;
        public string? Notes { get; set; }
    }
}
