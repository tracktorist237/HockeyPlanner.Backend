using HockeyPlanner.Backend.Core.Enums;
using HockeyPlanner.Backend.WebAPI.Models.ExternalLeagues;

namespace HockeyPlanner.Backend.WebAPI.Services
{
    public interface IExternalLeagueProvider
    {
        ExternalLeagueProvider Provider { get; }
        Task<IReadOnlyCollection<ExternalTeamSearchItem>> SearchTeamsAsync(string title, CancellationToken cancellationToken);
        Task<ExternalTeamProfile?> GetTeamProfileAsync(string externalTeamId, CancellationToken cancellationToken);
        Task<IReadOnlyCollection<ExternalMatch>> GetTeamScheduleAsync(string externalTeamId, CancellationToken cancellationToken);
        Task<ExternalMatchDetails?> GetMatchDetailsAsync(
            string externalCompetitionId,
            string externalMatchId,
            CancellationToken cancellationToken);
    }

    public interface IExternalLeagueProviderResolver
    {
        IExternalLeagueProvider Resolve(ExternalLeagueProvider provider);
    }
}
