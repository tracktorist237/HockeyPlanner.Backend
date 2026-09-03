using HockeyPlanner.Backend.Core.Entities.Base;
using HockeyPlanner.Backend.Core.Enums;

namespace HockeyPlanner.Backend.Core.Entities
{
    public class TeamExternalLeagueLink : Entity
    {
        public Guid TeamId { get; set; }
        public Team Team { get; set; } = null!;
        public ExternalLeagueProvider Provider { get; set; }
        public string ExternalTeamId { get; set; } = string.Empty;
        public string ExternalTeamName { get; set; } = string.Empty;
        public string? DivisionName { get; set; }
        public string? ProfileUrl { get; set; }
        public string? LogoUrl { get; set; }
        public string? CoverUrl { get; set; }
        public string? City { get; set; }
        public string? Country { get; set; }
        public bool IsPrimary { get; set; }
        public DateTime? LastSyncAttemptAt { get; set; }
        public DateTime? LastSuccessfulSyncAt { get; set; }
    }
}
