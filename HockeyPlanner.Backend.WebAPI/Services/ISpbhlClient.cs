using HockeyPlanner.Backend.WebAPI.Models.Spbhl;

namespace HockeyPlanner.Backend.WebAPI.Services
{
    public interface ISpbhlClient
    {
        Task<IReadOnlyCollection<SpbhlTeamSearchItem>> SearchTeamsAsync(
            string? title,
            CancellationToken cancellationToken);

        Task<IReadOnlyCollection<SpbhlMatchItem>> GetTeamScheduleAsync(
            Guid teamId,
            CancellationToken cancellationToken);
    }
}
