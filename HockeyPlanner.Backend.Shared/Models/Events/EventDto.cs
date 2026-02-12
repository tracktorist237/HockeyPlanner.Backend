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
        public EventStatus Status { get; set; }

        public string LocationName { get; set; } = string.Empty;
        public string LocationAddress { get; set; } = string.Empty;
        public string? IceRinkNumber { get; set; }

        public List<LineDto> Roster { get; set; } = new();

        public List<AttendanceLookUpDto> Attendances { get; set; } = new();
        public DateTime CreatedAt { get; set; }
        public DateTime? UpdatedAt { get; set; }

        public string? HomeTeamName { get; set; }
        public string? AwayTeamName { get; set; }
        public string? LeagueName { get; set; }
    }
}
