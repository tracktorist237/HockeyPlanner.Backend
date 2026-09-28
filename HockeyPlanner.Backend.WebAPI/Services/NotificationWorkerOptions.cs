namespace HockeyPlanner.Backend.WebAPI.Services;

public sealed class NotificationWorkerOptions
{
    public const string SectionName = "NotificationWorker";
    public bool Enabled { get; set; } = true;
    public int BatchSize { get; set; } = 20;
    public int PollIntervalSeconds { get; set; } = 10;
    public int MaxAttempts { get; set; } = 5;
    public int RetryDelaySeconds { get; set; } = 30;
    public int ClaimTimeoutSeconds { get; set; } = 180;
    public int DeliveryTimeoutSeconds { get; set; } = 30;
}
