using HockeyPlanner.Backend.Shared.Models.Teams;
using HockeyPlanner.Backend.WebAPI.Models.Teams;

namespace HockeyPlanner.Backend.WebAPI.Services;

public interface ITeamMediaUploadService
{
    Task<TeamDto> UploadTeamMedia(Guid id, Guid actorUserId, IFormFile file, bool isCover, CancellationToken cancellationToken);
    Task<UploadTeamImageResponse> UploadNewsImage(Guid id, Guid actorUserId, IFormFile file, CancellationToken cancellationToken);
}
