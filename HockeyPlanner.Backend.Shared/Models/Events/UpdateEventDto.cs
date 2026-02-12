using HockeyPlanner.Backend.Core.Enums;
using System;
using System.Collections.Generic;
using System.Text;

namespace HockeyPlanner.Backend.Shared.Models.Events
{
    public class UpdateEventDto
    {
        public string? Title { get; set; }
        public string? Description { get; set; }
        public DateTime? StartTime { get; set; }

        public string? LocationName { get; set; }
        public string? LocationAddress { get; set; }
        public string? IceRinkNumber { get; set; }

        public string? FocusArea { get; set; }
        public EventStatus? Status { get; set; }
    }
}
