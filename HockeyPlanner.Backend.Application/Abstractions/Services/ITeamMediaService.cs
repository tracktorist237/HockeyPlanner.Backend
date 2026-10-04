using HockeyPlanner.Backend.Shared.Models.Teams;

namespace HockeyPlanner.Backend.Application.Abstractions.Services;

// Persistence/authorization half of uploads. Storage remains behind the existing WebAPI adapter.
public interface ITeamMediaService
{
    Task EnsureCanUploadTeamMedia(Guid id, Guid actorUserId, CancellationToken cancellationToken);
    Task EnsureCanUploadNewsImage(Guid id, Guid actorUserId, CancellationToken cancellationToken);
    Task<TeamDto> SaveTeamMedia(Guid id, Guid actorUserId, string publicUrl, bool isCover, CancellationToken cancellationToken);
}
