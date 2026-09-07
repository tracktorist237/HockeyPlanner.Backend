using HockeyPlanner.Backend.Application.Abstractions.Services;
using HockeyPlanner.Backend.Core.Enums;
using HockeyPlanner.Backend.Infrastructure.Data;
using HockeyPlanner.Backend.Shared.Models.Events;
using Microsoft.EntityFrameworkCore;

namespace HockeyPlanner.Backend.Application.Implementations.Services;

internal sealed class EventConflictService(AppDbContext context) : IEventConflictService
{
    private static readonly TimeSpan PostEventBuffer = TimeSpan.FromHours(1);

    public async Task<IReadOnlyDictionary<Guid, IReadOnlyCollection<EventConflictDto>>> GetTeamConflictsAsync(
        IReadOnlyCollection<Guid> teamIds,
        CancellationToken cancellationToken)
    {
        if (teamIds.Count == 0) return new Dictionary<Guid, IReadOnlyCollection<EventConflictDto>>();

        var events = await context.Events.AsNoTracking()
            .Where(value => value.TeamId.HasValue && teamIds.Contains(value.TeamId.Value) && value.Status != EventStatus.Cancelled)
            .Select(value => new ConflictEvent(value.Id, value.TeamId!.Value, value.Title, value.StartTime,
                value.DurationMinutes, value.Status, value.Team == null ? null : value.Team.Name))
            .ToListAsync(cancellationToken);
        var result = events.ToDictionary(value => value.Id, _ => new List<EventConflictDto>());

        foreach (var group in events.GroupBy(value => value.TeamId))
        {
            var ordered = group.OrderBy(value => value.StartTime).ToList();
            for (var index = 0; index < ordered.Count; index++)
            {
                var current = ordered[index];
                for (var otherIndex = index + 1; otherIndex < ordered.Count; otherIndex++)
                {
                    var other = ordered[otherIndex];
                    if (!Overlaps(current, other))
                    {
                        if (other.StartTime >= BufferedEnd(current)) break;
                        continue;
                    }
                    result[current.Id].Add(ToDto(other));
                    result[other.Id].Add(ToDto(current));
                }
            }
        }

        return result.ToDictionary(pair => pair.Key, pair => (IReadOnlyCollection<EventConflictDto>)pair.Value
            .OrderBy(value => value.StartTime).ToList());
    }

    public async Task<IReadOnlyCollection<EventConflictDto>> GetPersonalConflictsAsync(
        Guid userId,
        Guid eventId,
        CancellationToken cancellationToken)
    {
        var current = await context.Events.AsNoTracking()
            .Where(value => value.Id == eventId && value.Status != EventStatus.Cancelled)
            .Select(value => new ConflictEvent(value.Id, value.TeamId, value.Title, value.StartTime,
                value.DurationMinutes, value.Status, value.Team == null ? null : value.Team.Name))
            .SingleOrDefaultAsync(cancellationToken);
        if (current is null) return [];

        var teamIds = context.TeamMemberships.AsNoTracking().Where(value => value.UserId == userId).Select(value => value.TeamId);
        var confirmed = await context.Attendances.AsNoTracking()
            .Where(value => value.UserId == userId && value.Status == AttendanceStatus.Confirmed &&
                value.EventId != eventId && value.Event.Status != EventStatus.Cancelled &&
                value.Event.TeamId.HasValue && teamIds.Contains(value.Event.TeamId.Value))
            .Select(value => new ConflictEvent(value.Event.Id, value.Event.TeamId, value.Event.Title,
                value.Event.StartTime, value.Event.DurationMinutes, value.Event.Status,
                value.Event.Team == null ? null : value.Event.Team.Name))
            .ToListAsync(cancellationToken);

        return confirmed.Where(other => Overlaps(current, other)).OrderBy(value => value.StartTime).Select(ToDto).ToList();
    }

    private static bool Overlaps(ConflictEvent left, ConflictEvent right) =>
        left.StartTime < BufferedEnd(right) && right.StartTime < BufferedEnd(left);

    private static DateTime BufferedEnd(ConflictEvent value) =>
        value.StartTime.AddMinutes(value.DurationMinutes).Add(PostEventBuffer);

    private static EventConflictDto ToDto(ConflictEvent value) => new()
    {
        Id = value.Id,
        Title = value.Title,
        StartTime = value.StartTime,
        DurationMinutes = value.DurationMinutes,
        Status = value.Status,
        TeamName = value.TeamName
    };

    private sealed record ConflictEvent(Guid Id, Guid? TeamId, string Title, DateTime StartTime,
        int DurationMinutes, EventStatus Status, string? TeamName);
}
