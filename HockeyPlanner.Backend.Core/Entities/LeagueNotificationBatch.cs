using HockeyPlanner.Backend.Core.Entities.Base;

namespace HockeyPlanner.Backend.Core.Entities;

public sealed class LeagueNotificationBatch : Entity
{
    public Guid TeamId { get; set; }
    public bool Background { get; set; }
    public string ChangesJson { get; set; } = "[]";
    public DateTime? CompletedAt { get; set; }
}
