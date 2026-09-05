using HockeyPlanner.Backend.WebAPI.Models.Spbhl;

namespace HockeyPlanner.Backend.WebAPI.Services
{
    public interface ISpbhlTeamHtmlParser
    {
        IReadOnlyCollection<SpbhlTeamSearchItem> ParseTeams(string html);
    }
}
