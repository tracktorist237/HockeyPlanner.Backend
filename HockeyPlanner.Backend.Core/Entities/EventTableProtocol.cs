using HockeyPlanner.Backend.Core.Entities.Base;

namespace HockeyPlanner.Backend.Core.Entities
{
    public class EventTableProtocol : Entity
    {
        public Guid EventId { get; set; }
        public ScheduledEvent Event { get; set; } = null!;

        public Guid TeamTableId { get; set; }
        public TeamTable TeamTable { get; set; } = null!;

        public Guid CreatedByUserId { get; set; }
        public User CreatedByUser { get; set; } = null!;

        public ICollection<EventTableProtocolRow> Rows { get; set; } = new List<EventTableProtocolRow>();
    }
}
