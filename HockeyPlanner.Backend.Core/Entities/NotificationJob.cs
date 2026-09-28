using HockeyPlanner.Backend.Core.Entities.Base;
using HockeyPlanner.Backend.Core.Enums;

namespace HockeyPlanner.Backend.Core.Entities;

// The notification holds the message. Jobs never copy push endpoints or credentials.
public sealed class NotificationJob : Entity
{
    public Guid NotificationId { get; set; }
    public Notification Notification { get; set; } = null!;
    public NotificationJobStatus Status { get; set; }
    public int AttemptCount { get; set; }
    public DateTime NextAttemptAt { get; set; }
    public DateTime? ClaimedAt { get; set; }
    public Guid? ClaimId { get; set; }
    public DateTime? CompletedAt { get; set; }
    public string? LastErrorCode { get; set; }
}
