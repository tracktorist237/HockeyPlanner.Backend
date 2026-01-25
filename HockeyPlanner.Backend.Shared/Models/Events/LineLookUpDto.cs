using HockeyPlanner.Backend.Core.Entities;
using System;
using System.Collections.Generic;
using System.Text;

namespace HockeyPlanner.Backend.Shared.Models.Events
{
    public class LineLookupDto
    {
        public string Name { get; set; } = string.Empty; // "Первая пятерка",
        public int Order { get; set; } = 1;

        public List<PlayerLookUpDto> Members { get; set; } = new();
    }
}
