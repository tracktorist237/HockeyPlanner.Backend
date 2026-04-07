namespace HockeyPlanner.Backend.WebAPI.Models.Spbhl
{
    public class SpbhlPlayersSearchResponse
    {
        public int Page { get; set; }
        public int TotalPages { get; set; }
        public IReadOnlyCollection<SpbhlPlayerSearchItem> Players { get; set; } = [];
    }
}
