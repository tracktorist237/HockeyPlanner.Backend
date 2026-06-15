using HockeyPlanner.Backend.Core.Entities.Base;

namespace HockeyPlanner.Backend.Core.Entities
{
    public class EventTableProtocolRow : Entity
    {
        public Guid EventTableProtocolId { get; set; }
        public EventTableProtocol EventTableProtocol { get; set; } = null!;

        public Guid UserId { get; set; }
        public User User { get; set; } = null!;

        public int Games { get; set; }
        public int Goals { get; set; }
        public int Assists { get; set; }
        public int Points { get; set; }
    }
}
