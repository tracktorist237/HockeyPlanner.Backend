using System.Net;
using System.Text.Json;
using WebPush;
using PushSubscriptionEntity = HockeyPlanner.Backend.Core.Entities.PushSubscription;
using WebPushSubscription = WebPush.PushSubscription;

namespace HockeyPlanner.Backend.WebAPI.Services
{
    public class WebPushService : IWebPushService
    {
        private readonly ILogger<WebPushService> _logger;
        private readonly WebPushClient _webPushClient;
        private readonly VapidDetails? _vapidDetails;

        public bool IsConfigured => _vapidDetails != null;

        public WebPushService(IConfiguration configuration, ILogger<WebPushService> logger)
        {
            _logger = logger;
            _webPushClient = new WebPushClient();

            var publicKey = configuration["VAPID_PUBLIC_KEY"] ?? Environment.GetEnvironmentVariable("VAPID_PUBLIC_KEY");
            var privateKey = configuration["VAPID_PRIVATE_KEY"] ?? Environment.GetEnvironmentVariable("VAPID_PRIVATE_KEY");
            var subject = configuration["VAPID_SUBJECT"] ?? Environment.GetEnvironmentVariable("VAPID_SUBJECT") ?? "mailto:admin@hockey-planner.local";

            if (string.IsNullOrWhiteSpace(publicKey) || string.IsNullOrWhiteSpace(privateKey))
            {
                _logger.LogWarning("VAPID keys are not configured. Web push notifications are disabled.");
                return;
            }

            _vapidDetails = new VapidDetails(subject, publicKey, privateKey);
        }

        public async Task<WebPushSendResult> SendAsync(
            PushSubscriptionEntity subscription,
            object payload,
            CancellationToken cancellationToken = default)
        {
            if (!IsConfigured || _vapidDetails == null)
            {
                return new WebPushSendResult
                {
                    IsSuccess = false,
                    ShouldRemoveSubscription = false,
                    Error = "VAPID is not configured"
                };
            }

            try
            {
                cancellationToken.ThrowIfCancellationRequested();

                var targetSubscription = new WebPushSubscription(
                    subscription.Endpoint,
                    subscription.P256dhKey,
                    subscription.AuthKey);

                var payloadJson = JsonSerializer.Serialize(payload);
                await _webPushClient.SendNotificationAsync(targetSubscription, payloadJson, _vapidDetails);

                return new WebPushSendResult { IsSuccess = true };
            }
            catch (WebPushException exception)
            {
                var statusCode = exception.StatusCode;
                var shouldRemove = statusCode == HttpStatusCode.Gone || statusCode == HttpStatusCode.NotFound;
                _logger.LogWarning(
                    exception,
                    "Web push send failed for endpoint {Endpoint}. Status: {StatusCode}",
                    subscription.Endpoint,
                    statusCode);

                return new WebPushSendResult
                {
                    IsSuccess = false,
                    ShouldRemoveSubscription = shouldRemove,
                    Error = exception.Message
                };
            }
            catch (Exception exception)
            {
                _logger.LogError(exception, "Unexpected web push error for endpoint {Endpoint}", subscription.Endpoint);
                return new WebPushSendResult
                {
                    IsSuccess = false,
                    ShouldRemoveSubscription = false,
                    Error = exception.Message
                };
            }
        }
    }
}
