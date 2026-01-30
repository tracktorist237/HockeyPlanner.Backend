using HockeyPlanner.Backend.Core.Enums;
using System;
using System.Collections.Generic;
using System.Text;

namespace HockeyPlanner.Backend.Shared.Models.Lines
{
    public class CreatePlayerData
    {
        public Guid UserId { get; set; }
        public PlayerRole Role { get; set; }
    }
}
