using HockeyPlanner.Backend.Core.Enums;
using System;
using System.Collections.Generic;
using System.Text;

namespace HockeyPlanner.Backend.Shared.Models.Events
{
    // Для создания мероприятия
    public class CreateEventDto
    {
        public string Title { get; set; } = string.Empty;
        public string? Description { get; set; }
        public EventType Type { get; set; }
        public DateTime StartTime { get; set; }

        public string LocationName { get; set; } = string.Empty;
        public string LocationAddress { get; set; } = string.Empty;
        public string? IceRinkNumber { get; set; }

        // Для игр
        public string? HomeTeamName { get; set; }
        public string? AwayTeamName { get; set; }
        public string? LeagueName { get; set; }

        // Для тренировок
        public List<Guid> ExerciseIds { get; set; } = new();
    }
}
