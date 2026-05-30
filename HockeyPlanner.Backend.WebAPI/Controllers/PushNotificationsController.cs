using HockeyPlanner.Backend.Application.Abstractions.Services;
using HockeyPlanner.Backend.Core.Entities;
using HockeyPlanner.Backend.Core.Enums;
using HockeyPlanner.Backend.Infrastructure.Data;
using HockeyPlanner.Backend.WebAPI.Extensions;
using HockeyPlanner.Backend.WebAPI.Models.Push;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace HockeyPlanner.Backend.WebAPI.Controllers
{
    [ApiController]
    [Route("api/push")]
    public class PushNotificationsController : ControllerBase
    {
        private readonly AppDbContext _context;
        private readonly INotificationService _notificationService;
        private readonly ILogger<PushNotificationsController> _logger;
        private readonly string? _vapidPublicKey;

        public PushNotificationsController(
            AppDbContext context,
            IConfiguration configuration,
            INotificationService notificationService,
            ILogger<PushNotificationsController> logger)
        {
            _context = context;
            _notificationService = notificationService;
            _logger = logger;
            _vapidPublicKey = configuration["Vapid:PublicKey"];
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
            var now = DateTime.UtcNow;

            if (existing is null)
            {
                var subscription = new PushSubscription
                {
                    Endpoint = request.Endpoint.Trim(),
                    P256dhKey = request.Keys.P256dh.Trim(),
                    AuthKey = request.Keys.Auth.Trim(),
                    UserId = request.UserId,
                    UserAgent = string.IsNullOrWhiteSpace(request.UserAgent) ? null : request.UserAgent.Trim(),
                    Platform = string.IsNullOrWhiteSpace(request.Platform) ? null : request.Platform.Trim(),
                    DeviceName = string.IsNullOrWhiteSpace(request.DeviceName) ? null : request.DeviceName.Trim(),
                    IsActive = true,
                    LastSeenAt = now,
                    RevokedAt = null,
                    CreatedAt = now,
                    UpdatedAt = now,
                };

                await _context.PushSubscriptions.AddAsync(subscription);
            }
            else
            {
                existing.P256dhKey = request.Keys.P256dh.Trim();
                existing.AuthKey = request.Keys.Auth.Trim();
                existing.UserId = request.UserId;
                existing.UserAgent = string.IsNullOrWhiteSpace(request.UserAgent) ? null : request.UserAgent.Trim();
                existing.Platform = string.IsNullOrWhiteSpace(request.Platform) ? null : request.Platform.Trim();
                existing.DeviceName = string.IsNullOrWhiteSpace(request.DeviceName) ? null : request.DeviceName.Trim();
                existing.IsActive = true;
                existing.LastSeenAt = now;
                existing.RevokedAt = null;
                existing.UpdatedAt = now;
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
                existing.IsActive = false;
                existing.RevokedAt = DateTime.UtcNow;
                existing.UpdatedAt = DateTime.UtcNow;
                await _context.SaveChangesAsync();
            }

            return Ok(new { success = true });
        }

        [Authorize]
        [HttpPost("broadcast")]
        public async Task<IActionResult> Broadcast([FromBody] PushBroadcastRequest request, CancellationToken cancellationToken)
        {
            if (!await this.IsSuperAdminAsync(_context, cancellationToken))
            {
                return Forbid();
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

            var userIds = await _context.PushSubscriptions
                .AsNoTracking()
                .Where(subscription => subscription.IsActive && subscription.UserId.HasValue)
                .Select(subscription => subscription.UserId!.Value)
                .Distinct()
                .ToListAsync(cancellationToken);
            if (userIds.Count == 0)
            {
                return Ok(new { success = true, total = 0 });
            }

            await _notificationService.NotifyUsersAsync(
                userIds,
                NotificationType.AppUpdatePublished,
                NotificationCategory.AppUpdates,
                title,
                body,
                url,
                cancellationToken);

            _logger.LogInformation(
                "Push broadcast notification created for {UserCount} users.",
                userIds.Count);

            return Ok(new
            {
                success = true,
                total = userIds.Count
            });
        }
    }
}
