using HockeyPlanner.Backend.Core.Entities;
using HockeyPlanner.Backend.Infrastructure.Data;
using HockeyPlanner.Backend.WebAPI.Models.Push;
using HockeyPlanner.Backend.WebAPI.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace HockeyPlanner.Backend.WebAPI.Controllers
{
    [ApiController]
    [Route("api/push")]
    public class PushNotificationsController : ControllerBase
    {
        private readonly AppDbContext _context;
        private readonly IWebPushService _webPushService;
        private readonly ILogger<PushNotificationsController> _logger;
        private readonly string? _vapidPublicKey;

        public PushNotificationsController(
            AppDbContext context,
            IConfiguration configuration,
            IWebPushService webPushService,
            ILogger<PushNotificationsController> logger)
        {
            _context = context;
            _webPushService = webPushService;
            _logger = logger;
            _vapidPublicKey = configuration["VAPID_PUBLIC_KEY"] ?? Environment.GetEnvironmentVariable("VAPID_PUBLIC_KEY");
        }

        [HttpGet("public-key")]
        public ActionResult<object> GetPublicKey()
        {
            if (string.IsNullOrWhiteSpace(_vapidPublicKey))
            {
                return NotFound(new { message = "VAPID public key is not configured." });
            }

            return Ok(new { publicKey = _vapidPublicKey });
        }

        [HttpPost("subscribe")]
        public async Task<IActionResult> Subscribe([FromBody] PushSubscriptionRequest request)
        {
            if (string.IsNullOrWhiteSpace(request.Endpoint) ||
                string.IsNullOrWhiteSpace(request.Keys?.P256dh) ||
                string.IsNullOrWhiteSpace(request.Keys?.Auth))
            {
                return BadRequest(new { message = "Invalid push subscription payload." });
            }

            var existing = await _context.PushSubscriptions
                .FirstOrDefaultAsync(subscription => subscription.Endpoint == request.Endpoint);

            if (existing is null)
            {
                var subscription = new PushSubscription
                {
                    Endpoint = request.Endpoint.Trim(),
                    P256dhKey = request.Keys.P256dh.Trim(),
                    AuthKey = request.Keys.Auth.Trim(),
                    UserId = request.UserId,
                    UserAgent = string.IsNullOrWhiteSpace(request.UserAgent) ? null : request.UserAgent.Trim(),
                    CreatedAt = DateTime.UtcNow,
                    UpdatedAt = DateTime.UtcNow,
                };

                await _context.PushSubscriptions.AddAsync(subscription);
            }
            else
            {
                existing.P256dhKey = request.Keys.P256dh.Trim();
                existing.AuthKey = request.Keys.Auth.Trim();
                existing.UserId = request.UserId;
                existing.UserAgent = string.IsNullOrWhiteSpace(request.UserAgent) ? null : request.UserAgent.Trim();
                existing.UpdatedAt = DateTime.UtcNow;
            }

            await _context.SaveChangesAsync();
            return Ok(new { success = true });
        }

        [HttpPost("unsubscribe")]
        public async Task<IActionResult> Unsubscribe([FromBody] PushUnsubscribeRequest request)
        {
            if (string.IsNullOrWhiteSpace(request.Endpoint))
            {
                return BadRequest(new { message = "Endpoint is required." });
            }

            var existing = await _context.PushSubscriptions
                .FirstOrDefaultAsync(subscription => subscription.Endpoint == request.Endpoint);

            if (existing is not null)
            {
                _context.PushSubscriptions.Remove(existing);
                await _context.SaveChangesAsync();
            }

            return Ok(new { success = true });
        }

        [HttpPost("broadcast")]
        public async Task<IActionResult> Broadcast([FromBody] PushBroadcastRequest request, CancellationToken cancellationToken)
        {
            if (!_webPushService.IsConfigured)
            {
                return BadRequest(new { message = "VAPID is not configured." });
            }

            var title = request.Title?.Trim();
            var body = request.Body?.Trim();
            var url = string.IsNullOrWhiteSpace(request.Url) ? "/events" : request.Url.Trim();

            if (string.IsNullOrWhiteSpace(title))
            {
                return BadRequest(new { message = "Title is required." });
            }

            if (string.IsNullOrWhiteSpace(body))
            {
                return BadRequest(new { message = "Body is required." });
            }

            var subscriptions = await _context.PushSubscriptions.ToListAsync(cancellationToken);
            if (subscriptions.Count == 0)
            {
                return Ok(new { success = true, total = 0, sent = 0, removed = 0 });
            }

            var payload = new { title, body, url };
            var sent = 0;
            var removed = 0;

            foreach (var subscription in subscriptions)
            {
                var result = await _webPushService.SendAsync(subscription, payload, cancellationToken);
                if (result.IsSuccess)
                {
                    sent++;
                    continue;
                }

                if (result.ShouldRemoveSubscription)
                {
                    _context.PushSubscriptions.Remove(subscription);
                    removed++;
                }
            }

            if (removed > 0)
            {
                await _context.SaveChangesAsync(cancellationToken);
            }

            _logger.LogInformation(
                "Push broadcast sent. Total: {Total}, Sent: {Sent}, Removed: {Removed}",
                subscriptions.Count,
                sent,
                removed);

            return Ok(new
            {
                success = true,
                total = subscriptions.Count,
                sent,
                removed
            });
        }
    }
}
