namespace HockeyPlanner.Backend.WebAPI.Models.Spbhl
{
    public class SpbhlTeamSearchItem
    {
        public Guid TeamId { get; set; }
        public string Name { get; set; } = string.Empty;
        public string? City { get; set; }
        public string? Country { get; set; }
        public string? LogoUrl { get; set; }
        public string ProfileUrl { get; set; } = string.Empty;
        public int? TournamentId { get; set; }
        public string? DivisionName { get; set; }
    }
}
