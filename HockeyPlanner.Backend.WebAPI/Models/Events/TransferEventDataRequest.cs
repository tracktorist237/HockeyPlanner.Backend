using HockeyPlanner.Backend.Core.Enums;

namespace HockeyPlanner.Backend.WebAPI.Models.Events;

public sealed class TransferEventDataRequest
{
    public Guid TargetEventId { get; set; }
    public bool Attendance { get; set; }
    public bool Roster { get; set; }
    public bool Guests { get; set; }
    public bool UniformColor { get; set; }
    public bool Description { get; set; }
    public bool DeleteSourceEvent { get; set; }
    public AttendanceTransferMode AttendanceTransferMode { get; set; } = AttendanceTransferMode.MergePreferTarget;
}

public enum AttendanceTransferMode
{
    ReplaceTarget = 1,
    MergePreferTarget = 2,
    ConfirmedOnly = 3
}

public sealed class PreviewAttendanceTransferRequest
{
    public Guid TargetEventId { get; set; }
    public AttendanceTransferMode AttendanceTransferMode { get; set; } = AttendanceTransferMode.MergePreferTarget;
}

public sealed class AttendanceTransferPreviewDto
{
    public IReadOnlyCollection<AttendanceTransferPreviewItemDto> Items { get; init; } = [];
    public int ChangedCount => Items.Count(value => value.WillChange);
}

public sealed class AttendanceTransferPreviewItemDto
{
    public Guid UserId { get; init; }
    public string? UserDisplayName { get; init; }
    public AttendanceStatus SourceStatus { get; init; }
    public AttendanceStatus? TargetStatus { get; init; }
    public AttendanceStatus? ResultingStatus { get; init; }
    public bool WillChange { get; init; }
}
