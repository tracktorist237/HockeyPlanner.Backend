using HockeyPlanner.Backend.Core.Enums;

namespace HockeyPlanner.Backend.Shared.Models.Events
{
    public class EventLookUpDto
    {
        public Guid Id { get; set; }
        public string Title { get; set; } = string.Empty;
        public string? Description { get; set; }
        public EventType Type { get; set; }
        public DateTime StartTime { get; set; }
        public EventStatus Status { get; set; }
        public AttendanceStatus? AttendanceStatus { get; set; }

        public string LocationName { get; set; } = string.Empty;
        public string LocationAddress { get; set; } = string.Empty;
        public string? IceRinkNumber { get; set; }
        public string? LeagueName { get; set; }
        public Guid? UniformColorId { get; set; }
        public Guid? TeamId { get; set; }
    }
}
