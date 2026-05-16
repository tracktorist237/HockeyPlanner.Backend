using HockeyPlanner.Backend.Core.Entities.Base;
using HockeyPlanner.Backend.Core.Enums;

namespace HockeyPlanner.Backend.Core.Entities
{
    public class GoalieRequest : Entity
    {
        public Guid EventId { get; set; }
        public ScheduledEvent Event { get; set; } = null!;

        public Guid? TeamId { get; set; }
        public Team? Team { get; set; }

        public Guid CreatedByUserId { get; set; }
        public User CreatedByUser { get; set; } = null!;

        public int NeededCount { get; set; }
        public GoalieRequestVisibility Visibility { get; set; }
        public GoalieRequestResponseMode ResponseMode { get; set; }
        public GoalieRequestStatus Status { get; set; }
        public string? PriceText { get; set; }
        public string? Description { get; set; }

        public ICollection<GoalieApplication> Applications { get; set; } = new List<GoalieApplication>();
    }
}
