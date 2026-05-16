using HockeyPlanner.Backend.Core.Entities.Base;
using HockeyPlanner.Backend.Core.Enums;

namespace HockeyPlanner.Backend.Core.Entities
{
    public class TeamMembership : Entity
    {
        public Guid TeamId { get; set; }
        public Guid UserId { get; set; }
        public TeamMemberRole Role { get; set; }
        public string? BadgeTitle { get; set; }

        public Team Team { get; set; } = null!;
        public User User { get; set; } = null!;
    }
}
