namespace HockeyPlanner.Backend.WebAPI.Models.Spbhl
{
    public class SpbhlPlayerSearchItem
    {
        public Guid PlayerId { get; set; }
        public string FullName { get; set; } = string.Empty;
        public string? BirthDate { get; set; }
        public string? TeamName { get; set; }
        public int? JerseyNumber { get; set; }
        public string PhotoSmallUrl { get; set; } = string.Empty;
        public string PhotoLargeUrl { get; set; } = string.Empty;
        public string ProfileUrl { get; set; } = string.Empty;
    }
}
