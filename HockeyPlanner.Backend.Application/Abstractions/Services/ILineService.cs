using HockeyPlanner.Backend.Shared.Models.Events;
using HockeyPlanner.Backend.Shared.Models.Lines;

namespace HockeyPlanner.Backend.Application.Abstractions.Services
{
    public interface ILineService
    {
        Task<List<LineDto>> GetRosterByEvent(Guid eventId);

        Task<List<LineDto>> CreateRoster(CreateUpdateRosterRequest request, Guid currentUserId);

        Task<bool> RemoveRosterByEvent(Guid eventId, Guid currentUserId);

        Task<List<LineDto>> UpdateRoster(CreateUpdateRosterRequest request, Guid currentUserId);
    }
}
