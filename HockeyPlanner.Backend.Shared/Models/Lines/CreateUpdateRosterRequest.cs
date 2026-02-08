using System;
using System.Collections.Generic;
using System.Text;

namespace HockeyPlanner.Backend.Shared.Models.Lines
{
    public class CreateUpdateRosterRequest
    {
        public Guid EventId { get; set; }
        public List<CreateUpdateLineData> Lines { get; set; } = null!;
    }
}
