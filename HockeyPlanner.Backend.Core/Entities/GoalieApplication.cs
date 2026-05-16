using HockeyPlanner.Backend.Core.Entities.Base;
using HockeyPlanner.Backend.Core.Enums;

namespace HockeyPlanner.Backend.Core.Entities
{
    public class GoalieApplication : Entity
    {
        public Guid GoalieRequestId { get; set; }
        public GoalieRequest GoalieRequest { get; set; } = null!;

        public Guid GoalieUserId { get; set; }
        public User GoalieUser { get; set; } = null!;

        public GoalieApplicationStatus Status { get; set; }
        public GoalieApplicationSource Source { get; set; }
        public string? Message { get; set; }
    }
}
