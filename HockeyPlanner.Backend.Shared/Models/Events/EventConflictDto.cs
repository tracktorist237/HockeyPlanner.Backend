using HockeyPlanner.Backend.Core.Enums;

namespace HockeyPlanner.Backend.Shared.Models.Events;

public sealed class EventConflictDto
{
    public Guid Id { get; set; }
    public string Title { get; set; } = string.Empty;
    public DateTime StartTime { get; set; }
    public int DurationMinutes { get; set; }
    public EventStatus Status { get; set; }
    public string? TeamName { get; set; }
}
