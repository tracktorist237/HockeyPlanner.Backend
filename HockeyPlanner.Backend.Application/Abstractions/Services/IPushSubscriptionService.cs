namespace HockeyPlanner.Backend.Application.Abstractions.Services;

public sealed record PushSubscriptionInput(
    string Endpoint,
    string P256dh,
    string Auth,
    string? UserAgent,
    string? Platform,
    string? DeviceName);

public enum PushSubscriptionResult
{
    Success,
    Conflict,
}

public interface IPushSubscriptionService
{
    Task<PushSubscriptionResult> Subscribe(
        Guid actorUserId,
        PushSubscriptionInput input,
        CancellationToken cancellationToken);

    Task Unsubscribe(
        Guid actorUserId,
        string endpoint,
        CancellationToken cancellationToken);
}
