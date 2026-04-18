using HockeyPlanner.Backend.Core.Entities;

namespace HockeyPlanner.Backend.WebAPI.Services
{
    public class WebPushSendResult
    {
        public bool IsSuccess { get; set; }
        public bool ShouldRemoveSubscription { get; set; }
        public string? Error { get; set; }
    }

    public interface IWebPushService
    {
        bool IsConfigured { get; }
        Task<WebPushSendResult> SendAsync(PushSubscription subscription, object payload, CancellationToken cancellationToken = default);
    }
}

