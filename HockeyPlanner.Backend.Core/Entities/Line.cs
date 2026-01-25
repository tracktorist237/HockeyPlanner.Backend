using HockeyPlanner.Backend.Core.Entities.Base;

namespace HockeyPlanner.Backend.Core.Entities
{
    public class Line : Entity
    {
        public string Name { get; set; } = string.Empty; // "Первая пятерка",
        public int Order { get; set; } = 1;

        public Guid EventId { get; set; }
        public ScheduledEvent Event { get; set; } = null!;

        public List<Player> Players { get; set; } = new();
    }
}
