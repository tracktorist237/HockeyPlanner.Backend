namespace HockeyPlanner.Backend.WebAPI.Models.Push
{
    public class PushSubscriptionKeysRequest
    {
        public string P256dh { get; set; } = string.Empty;
        public string Auth { get; set; } = string.Empty;
    }

    public class PushSubscriptionRequest
    {
        public string Endpoint { get; set; } = string.Empty;
        public PushSubscriptionKeysRequest Keys { get; set; } = new();
        public Guid? UserId { get; set; }
        public string? UserAgent { get; set; }
    }

    public class PushUnsubscribeRequest
    {
        public string Endpoint { get; set; } = string.Empty;
    }

    public class PushBroadcastRequest
    {
        public string Title { get; set; } = string.Empty;
        public string Body { get; set; } = string.Empty;
        public string? Url { get; set; }
    }
}
