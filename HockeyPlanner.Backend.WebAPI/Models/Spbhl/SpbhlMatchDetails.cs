namespace HockeyPlanner.Backend.WebAPI.Models.Spbhl
{
    public class SpbhlMatchDetails
    {
        public int TournamentId { get; set; }
        public int MatchId { get; set; }
        public string HomeTeamName { get; set; } = string.Empty;
        public string AwayTeamName { get; set; } = string.Empty;
        public int? HomeScore { get; set; }
        public int? AwayScore { get; set; }
        public SpbhlMatchStatus Status { get; set; }
        public string? ArenaName { get; set; }
        public string? ArenaAddress { get; set; }
        public string? TournamentName { get; set; }
        public string? DivisionName { get; set; }
        public string MatchUrl { get; set; } = string.Empty;
    }
}
