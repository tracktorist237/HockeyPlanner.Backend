using HockeyPlanner.Backend.Core.Enums;

namespace HockeyPlanner.Backend.Shared.Models.Events
{
    public class AttendanceLookUpDto
    {
        public Guid UserId { get; set; }
        public int? JerseyNumber { get; set; }
        public string FirstName { get; set; } = string.Empty;
        public string LastName { get; set; } = string.Empty;
        public Position? PrimaryPosition { get; set; }
        public Handedness? Handedness { get; set; }
        public AttendanceStatus Status { get; set; }
        public DateTime RespondedAt { get; set; } = DateTime.UtcNow;
        public string? Notes { get; set; }
    }
}
