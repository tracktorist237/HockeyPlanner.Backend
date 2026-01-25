using HockeyPlanner.Backend.Core.Enums;
using System;
using System.Collections.Generic;
using System.Text;

namespace HockeyPlanner.Backend.Shared.Models.Events
{
    public class EventLookUpDto
    {
        public Guid Id { get; set; }
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
    }
}
