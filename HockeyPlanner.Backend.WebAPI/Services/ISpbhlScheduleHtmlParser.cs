using HockeyPlanner.Backend.WebAPI.Models.Spbhl;

namespace HockeyPlanner.Backend.WebAPI.Services
{
    public interface ISpbhlScheduleHtmlParser
    {
        IReadOnlyCollection<SpbhlMatchItem> ParseSchedule(string html);
    }
}
