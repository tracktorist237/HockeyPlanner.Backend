using System;
using System.Collections.Generic;
using System.Text;

namespace HockeyPlanner.Backend.Shared.Models.Lines
{
    public class CreateUpdateLineData
    {
        public string Name { get; set; } = string.Empty; // "Первая пятерка",
        public int Order { get; set; } = 1;
        public List<CreateUpdatePlayerData> Players { get; set; } = new List<CreateUpdatePlayerData>();
    }
}
