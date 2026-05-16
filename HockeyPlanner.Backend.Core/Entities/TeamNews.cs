using HockeyPlanner.Backend.Core.Entities.Base;

namespace HockeyPlanner.Backend.Core.Entities
{
    public class TeamNews : Entity
    {
        public Guid TeamId { get; set; }
        public Team Team { get; set; } = null!;
        public Guid AuthorUserId { get; set; }
        public User AuthorUser { get; set; } = null!;
        public string Title { get; set; } = string.Empty;
        public string Body { get; set; } = string.Empty;
    }
}
