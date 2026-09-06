using HockeyPlanner.Backend.Shared.Models.Events;

namespace HockeyPlanner.Backend.Application.Abstractions.Services;

public interface IEventConflictService
{
    Task<IReadOnlyDictionary<Guid, IReadOnlyCollection<EventConflictDto>>> GetTeamConflictsAsync(
        IReadOnlyCollection<Guid> teamIds,
        CancellationToken cancellationToken);

    Task<IReadOnlyCollection<EventConflictDto>> GetPersonalConflictsAsync(
        Guid userId,
        Guid eventId,
        CancellationToken cancellationToken);
}
