namespace HockeyPlanner.Backend.WebAPI.Models.Spbhl
{
    public class SpbhlTeamProfile
    {
        public Guid TeamId { get; set; }
        public string Name { get; set; } = string.Empty;
        public string? City { get; set; }
        public string? Country { get; set; }
        public string? DivisionName { get; set; }
        public string ProfileUrl { get; set; } = string.Empty;
        public string? LogoUrl { get; set; }
        public string? CoverUrl { get; set; }
        public int? FoundedYear { get; set; }
        public string? CoachName { get; set; }
        public string? AdministratorName { get; set; }
        public IReadOnlyCollection<string> Phones { get; set; } = Array.Empty<string>();
        public IReadOnlyCollection<string> WebsiteUrls { get; set; } = Array.Empty<string>();
    }
}
