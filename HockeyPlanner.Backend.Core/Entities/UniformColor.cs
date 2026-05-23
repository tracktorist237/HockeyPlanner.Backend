using HockeyPlanner.Backend.Core.Entities.Base;

namespace HockeyPlanner.Backend.Core.Entities
{
    public class UniformColor : Entity
    {
        public string Name { get; set; } = string.Empty;
        public string ImageUrl { get; set; } = string.Empty;
        public Guid CreatedByUserId { get; set; }
        public Guid? TeamId { get; set; }
        public Team? Team { get; set; }

        public List<ScheduledEvent> Events { get; set; } = new();
    }
}
