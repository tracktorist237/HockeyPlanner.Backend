using HockeyPlanner.Backend.Core.Enums;
using HockeyPlanner.Backend.WebAPI.Models.ExternalLeagues;

namespace HockeyPlanner.Backend.WebAPI.Services
{
    public interface IExternalLeagueManagementService
    {
        Task<IReadOnlyCollection<ExternalTeamSearchItem>> SearchTeamsAsync(
            ExternalLeagueProvider provider,
            string title,
            CancellationToken cancellationToken);
        Task<IReadOnlyCollection<ExternalLeagueLinkDto>> GetLinksAsync(
            Guid teamId,
            Guid actorUserId,
            CancellationToken cancellationToken);
        Task<ExternalLeagueLinkDto> CreateLinkAsync(
            Guid teamId,
            Guid actorUserId,
            CreateExternalLeagueLinkRequest request,
            CancellationToken cancellationToken);
        Task<AppliedTeamProfileDto> ApplyProfileAsync(
            Guid teamId,
            Guid linkId,
            Guid actorUserId,
            ApplyExternalLeagueProfileRequest request,
            CancellationToken cancellationToken);
        Task<IReadOnlyCollection<ExternalAddressCandidateDto>> GetAddressCandidatesAsync(
            Guid teamId,
            Guid actorUserId,
            CancellationToken cancellationToken);
        Task DeleteLinkAsync(Guid teamId, Guid linkId, Guid actorUserId, CancellationToken cancellationToken);
        Task<ExternalLeagueSyncResult> SyncLinkAsync(
            Guid teamId,
            Guid linkId,
            Guid actorUserId,
            CancellationToken cancellationToken);
        Task<IReadOnlyCollection<ExternalLeagueSyncResult>> SyncTeamAsync(
            Guid teamId,
            Guid actorUserId,
            CancellationToken cancellationToken);
    }
}
