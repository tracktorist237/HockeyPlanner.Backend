using HockeyPlanner.Backend.WebAPI.Models.Spbhl;

namespace HockeyPlanner.Backend.WebAPI.Services
{
    public interface ISpbhlTeamProfileHtmlParser
    {
        SpbhlTeamProfile? ParseTeamProfile(string html, Guid teamId);
    }
}
