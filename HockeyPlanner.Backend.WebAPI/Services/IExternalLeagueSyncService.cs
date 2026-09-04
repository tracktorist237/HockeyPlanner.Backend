using HockeyPlanner.Backend.Core.Enums;
using HockeyPlanner.Backend.WebAPI.Models.ExternalLeagues;

namespace HockeyPlanner.Backend.WebAPI.Services
{
    public interface IExternalLeagueSyncService
    {
        Task<ExternalLeagueSyncResult> SyncExternalLinkAsync(Guid linkId, CancellationToken cancellationToken);
        Task<IReadOnlyCollection<ExternalLeagueSyncResult>> SyncTeamExternalLinksAsync(
            Guid teamId,
            ExternalLeagueProvider? provider,
            CancellationToken cancellationToken);
    }
}
