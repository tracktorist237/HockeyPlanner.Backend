using HockeyPlanner.Backend.WebAPI.Models.Spbhl;

namespace HockeyPlanner.Backend.WebAPI.Services
{
    public interface ISpbhlMatchHtmlParser
    {
        SpbhlMatchDetails? ParseMatch(string html, int tournamentId, int matchId);
    }
}
