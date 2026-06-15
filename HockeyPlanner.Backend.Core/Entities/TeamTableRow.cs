using HockeyPlanner.Backend.Core.Entities.Base;

namespace HockeyPlanner.Backend.Core.Entities
{
    public class TeamTableRow : Entity
    {
        public Guid TeamTableId { get; set; }
        public TeamTable TeamTable { get; set; } = null!;

        public Guid UserId { get; set; }
        public User User { get; set; } = null!;

        public int Games { get; set; }
        public int Goals { get; set; }
        public int Assists { get; set; }
        public int Points { get; set; }
    }
}
