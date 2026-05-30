namespace HockeyPlanner.Backend.Core.Enums
{
    public enum NotificationDeliveryStatus : int
    {
        Pending = 1,
        Sent = 2,
        Failed = 3,
        Skipped = 4,
        EndpointInactive = 5
    }
}
