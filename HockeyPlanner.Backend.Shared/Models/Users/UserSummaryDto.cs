using HockeyPlanner.Backend.Core.Enums;

namespace HockeyPlanner.Backend.Shared.Models.Users;

public sealed class UserSummaryDto
{
    public Guid Id { get; set; }
    public string? PhotoUrl { get; set; }
    public Position? PrimaryPosition { get; set; }
}
