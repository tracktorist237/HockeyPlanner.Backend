using HockeyPlanner.Backend.Core.Entities.Base;
using HockeyPlanner.Backend.Core.Enums;

namespace HockeyPlanner.Backend.Core.Entities
{
    public class Player : Entity
    {
        public Guid LineId { get; set; }
        public Line Line { get; set; } = null!;

        public Guid UserId { get; set; }
        public User User { get; set; } = null!;

        public string FirstName { get; set; } = string.Empty;
        public string LastName { get; set; } = string.Empty;
        public PlayerRole Role { get; set; }
        public int? JerseyNumber { get; set; }
        public Handedness? Handedness { get; set; }
    }
}
