using HockeyPlanner.Backend.WebAPI.Models.Spbhl;

namespace HockeyPlanner.Backend.WebAPI.Services
{
    public interface ISpbhlTeamManagementService
    {
        Task<SpbhlTeamLinkStatusDto> GetStatusAsync(Guid teamId, Guid actorUserId, CancellationToken cancellationToken);
        Task<IReadOnlyCollection<SpbhlTeamSearchItem>> SearchTeamsAsync(Guid teamId, Guid actorUserId, string title, CancellationToken cancellationToken);
        Task<SpbhlTeamBindResult> BindAsync(Guid teamId, Guid actorUserId, BindSpbhlTeamRequest request, CancellationToken cancellationToken);
        Task<SpbhlTeamLinkStatusDto> UnbindAsync(Guid teamId, Guid actorUserId, CancellationToken cancellationToken);
        Task<SpbhlTeamSyncResult> SyncNowAsync(Guid teamId, Guid actorUserId, CancellationToken cancellationToken);
    }
}
