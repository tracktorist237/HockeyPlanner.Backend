namespace HockeyPlanner.Backend.WebAPI.Models.Spbhl
{
    public class SpbhlMatchItem
    {
        public int MatchId { get; set; }
        public int TournamentId { get; set; }
        public DateTimeOffset StartTime { get; set; }
        public Guid? HomeTeamId { get; set; }
        public string HomeTeamName { get; set; } = string.Empty;
        public Guid? AwayTeamId { get; set; }
        public string AwayTeamName { get; set; } = string.Empty;
        public string? ArenaName { get; set; }
        public string? ArenaAddress { get; set; }
        public Guid? ArenaId { get; set; }
        public string? TournamentName { get; set; }
        public string? DivisionName { get; set; }
        public int? HomeScore { get; set; }
        public int? AwayScore { get; set; }
        public SpbhlMatchStatus Status { get; set; }
        public string? RawStatus { get; set; }
        public string MatchUrl { get; set; } = string.Empty;
    }
}
