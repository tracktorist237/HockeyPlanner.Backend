using HockeyPlanner.Backend.Core.Entities.Base;
using HockeyPlanner.Backend.Core.Enums;


namespace HockeyPlanner.Backend.Core.Entities
{
    public class ScheduledEvent : Entity
    {
        public string Title { get; set; } = string.Empty;
        public string? Description { get; set; }
        public EventType Type { get; set; } // Practice, Game
        public DateTime StartTime { get; set; }
        public DateTime EndTime { get; set; }
        public EventStatus Status { get; set; }

        // Место проведения
        public string LocationName { get; set; } = string.Empty;
        public string LocationAddress { get; set; } = string.Empty;
        public string? IceRinkNumber { get; set; }

        // Игровые нюансы
        public List<Line> Roster { get; set; } = new();

        // Навигационные свойства
        public List<Attendance> Attendances { get; set; } = new();

        // Вычисляемые свойства
        public bool IsGame => Type == EventType.Game;
        public bool IsPractice => Type == EventType.Practice;
        public string LocationFull =>
            !string.IsNullOrEmpty(IceRinkNumber)
                ? $"{LocationName} ({IceRinkNumber})"
                : LocationName;
    }
}
