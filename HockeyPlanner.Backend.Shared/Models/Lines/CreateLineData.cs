using System;
using System.Collections.Generic;
using System.Text;

namespace HockeyPlanner.Backend.Shared.Models.Lines
{
    public class CreateLineData
    {
        public string Name { get; set; } = string.Empty; // "Первая пятерка",
        public int Order { get; set; } = 1;
        public List<CreatePlayerData> Players { get; set; } = new List<CreatePlayerData>();
    }
}
