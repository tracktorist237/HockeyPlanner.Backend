using HockeyPlanner.Backend.Core.Entities.Base;
using HockeyPlanner.Backend.Core.Enums;

namespace HockeyPlanner.Backend.Core.Entities;

public sealed class ExternalEventSuppression : Entity
{
    public Guid TeamId { get; set; }
    public Team Team { get; set; } = null!;
    public ExternalLeagueProvider ExternalLeagueProvider { get; set; }
    public string ExternalCompetitionId { get; set; } = string.Empty;
    public string ExternalMatchId { get; set; } = string.Empty;
    public Guid? CreatedByUserId { get; set; }
    public User? CreatedByUser { get; set; }
    public string? Reason { get; set; }
    public string? ExternalTitle { get; set; }
    public DateTime? StartTime { get; set; }
    public string? CompetitionName { get; set; }
}
