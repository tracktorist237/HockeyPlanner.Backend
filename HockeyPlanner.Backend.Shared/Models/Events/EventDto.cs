using HockeyPlanner.Backend.Core.Entities;
using HockeyPlanner.Backend.Core.Enums;

namespace HockeyPlanner.Backend.Shared.Models.Events
{
    public class EventDto
    {
        public Guid Id { get; set; }
        public string Title { get; set; } = string.Empty;
        public string? Description { get; set; }
        public EventType Type { get; set; } // Practice, Game
        public DateTime StartTime { get; set; }
        public DateTime EndTime { get; set; }
        public EventStatus Status { get; set; }

        public string LocationName { get; set; } = string.Empty;
        public string LocationAddress { get; set; } = string.Empty;
        public string? IceRinkNumber { get; set; }

        public List<LineDto> Roster { get; set; } = new();

        public List<AttendanceLookUpDto> Attendances { get; set; } = new();
        public DateTime CreatedAt { get; set; }
        public DateTime? UpdatedAt { get; set; }

        // Вычисляемые свойства
        public string LocationFull =>
            !string.IsNullOrEmpty(IceRinkNumber)
                ? $"{LocationName} ({IceRinkNumber})"
                : LocationName;

        public bool IsPast => EndTime < DateTime.UtcNow;
        public bool IsUpcoming => StartTime > DateTime.UtcNow;
        public bool IsOngoing => StartTime <= DateTime.UtcNow && EndTime >= DateTime.UtcNow;
    }
}
