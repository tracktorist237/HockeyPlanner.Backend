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
}
