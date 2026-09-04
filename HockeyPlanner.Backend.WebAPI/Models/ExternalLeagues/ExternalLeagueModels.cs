using HockeyPlanner.Backend.Core.Enums;

namespace HockeyPlanner.Backend.WebAPI.Models.ExternalLeagues
{
    public class ExternalTeamSearchItem
    {
        public ExternalLeagueProvider Provider { get; set; }
        public string ExternalTeamId { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public string? City { get; set; }
        public string? Country { get; set; }
        public string? LogoUrl { get; set; }
        public string? ProfileUrl { get; set; }
        public string? DivisionName { get; set; }
    }

    public class ExternalTeamProfile : ExternalTeamSearchItem
    {
        public string? CoverUrl { get; set; }
    }

    public class ExternalMatch
    {
        public string ExternalCompetitionId { get; set; } = string.Empty;
        public string ExternalMatchId { get; set; } = string.Empty;
        public int? LegacyNumericCompetitionId { get; set; }
        public int? LegacyNumericMatchId { get; set; }
        public DateTimeOffset StartTime { get; set; }
        public string HomeTeamName { get; set; } = string.Empty;
        public string AwayTeamName { get; set; } = string.Empty;
        public string? ArenaName { get; set; }
        public string? ArenaAddress { get; set; }
        public int? HomeScore { get; set; }
        public int? AwayScore { get; set; }
        public ExternalMatchStatus Status { get; set; }
        public string? TournamentName { get; set; }
        public string? DivisionName { get; set; }
        public string MatchUrl { get; set; } = string.Empty;
    }

    public class ExternalMatchDetails
    {
        public string ExternalCompetitionId { get; set; } = string.Empty;
        public string ExternalMatchId { get; set; } = string.Empty;
        public int? HomeScore { get; set; }
        public int? AwayScore { get; set; }
        public ExternalMatchStatus Status { get; set; }
        public string? ArenaName { get; set; }
        public string? ArenaAddress { get; set; }
        public string? TournamentName { get; set; }
        public string? DivisionName { get; set; }
    }

    public enum ExternalMatchStatus
    {
        Unknown,
        Scheduled,
        Finished
    }

    public class ExternalLeagueLinkDto
    {
        public Guid Id { get; set; }
        public Guid TeamId { get; set; }
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

    public class CreateExternalLeagueLinkRequest
    {
        public ExternalLeagueProvider Provider { get; set; }
        public string ExternalTeamId { get; set; } = string.Empty;
        public bool IsPrimary { get; set; }
    }

    public class ExternalLeagueSyncResult
    {
        public Guid TeamId { get; set; }
        public Guid LinkId { get; set; }
        public ExternalLeagueProvider Provider { get; set; }
        public string ExternalTeamId { get; set; } = string.Empty;
        public int ReceivedCount { get; set; }
        public int CreatedCount { get; set; }
        public int UpdatedCount { get; set; }
        public int UnchangedCount { get; set; }
        public int EnrichmentRequestCount { get; set; }
        public DateTime SyncedAt { get; set; }
    }
}
