using HockeyPlanner.Backend.Core.Entities.Base;
using HockeyPlanner.Backend.Core.Enums;

namespace HockeyPlanner.Backend.Core.Entities
{
    public class Team : Entity
    {
        public string Name { get; set; } = string.Empty;
        public string? Description { get; set; }
        public string? AvatarUrl { get; set; }
        public string? CoverImageUrl { get; set; }
        public string? PhoneContactsJson { get; set; }
        public string? LinkContactsJson { get; set; }
        public string? AddressContactsJson { get; set; }
        public TeamVisibility Visibility { get; set; }
        public string InviteCode { get; set; } = string.Empty;
        public Guid CreatedByUserId { get; set; }

        public ICollection<TeamMembership> Memberships { get; set; } = new List<TeamMembership>();
        public ICollection<ScheduledEvent> Events { get; set; } = new List<ScheduledEvent>();
        public ICollection<TeamNews> News { get; set; } = new List<TeamNews>();
    }
}
