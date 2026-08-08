using HockeyPlanner.Backend.Shared.Models.Events;
using HockeyPlanner.Backend.Shared.Models.Lines;

namespace HockeyPlanner.Backend.Application.Abstractions.Services
{
    public interface ILineService
    {
        Task<List<LineDto>> GetRosterByEvent(
            Guid eventId,
            Guid? viewerUserId,
            CancellationToken cancellationToken);

        Task<List<LineDto>> CreateRoster(
            CreateUpdateRosterRequest request,
            Guid actorUserId,
            CancellationToken cancellationToken);

        Task<bool> RemoveRosterByEvent(
            Guid eventId,
            Guid actorUserId,
            CancellationToken cancellationToken);

        Task<List<LineDto>> UpdateRoster(
            CreateUpdateRosterRequest request,
            Guid actorUserId,
            CancellationToken cancellationToken);
    }
}
