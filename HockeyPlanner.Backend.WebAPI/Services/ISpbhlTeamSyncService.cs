using HockeyPlanner.Backend.WebAPI.Models.Spbhl;

namespace HockeyPlanner.Backend.WebAPI.Services
{
    public interface ISpbhlTeamSyncService
    {
        Task<SpbhlTeamSyncResult> SyncTeamAsync(Guid teamId, CancellationToken cancellationToken);
    }
}
