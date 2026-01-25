using HockeyPlanner.Backend.Core.Enums;

namespace HockeyPlanner.Backend.Shared.Models.Events
{
    public class UpdateAttendanceRequest
    {
        public AttendanceStatus Status { get; set; }
        public string? Notes { get; set; }
    }
}
