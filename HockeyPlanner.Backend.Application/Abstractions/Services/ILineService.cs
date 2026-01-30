using HockeyPlanner.Backend.Shared.Models.Events;
using HockeyPlanner.Backend.Shared.Models.Lines;

namespace HockeyPlanner.Backend.Application.Abstractions.Services
{
    public interface ILineService
    {
        Task<List<LineDto>> GetRosterByEvent(Guid eventId);

        Task<List<LineDto>> CreateRoster(CreateRosterRequest request);

        Task<bool> RemoveRosterByEvent(Guid eventId);
    }
}
