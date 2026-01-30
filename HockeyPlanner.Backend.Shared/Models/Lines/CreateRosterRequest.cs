using System;
using System.Collections.Generic;
using System.Text;

namespace HockeyPlanner.Backend.Shared.Models.Lines
{
    public class CreateRosterRequest
    {
        public Guid EventId { get; set; }
        public List<CreateLineData> Lines { get; set; } = null!;
    }
}
