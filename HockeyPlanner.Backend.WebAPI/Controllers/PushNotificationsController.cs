using HockeyPlanner.Backend.Application.Abstractions.Services;
using HockeyPlanner.Backend.Application.Abstractions.Identity;
using HockeyPlanner.Backend.Core.Enums;
using HockeyPlanner.Backend.Infrastructure.Data;
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
        private readonly IPushSubscriptionService _pushSubscriptionService;
        private readonly ICurrentUser _currentUser;
        private readonly INotificationService _notificationService;
        private readonly ILogger<PushNotificationsController> _logger;
        private readonly string? _vapidPublicKey;

        public PushNotificationsController(
            AppDbContext context,
            IConfiguration configuration,
            IPushSubscriptionService pushSubscriptionService,
            ICurrentUser currentUser,
            INotificationService notificationService,
            ILogger<PushNotificationsController> logger)
        {
            _context = context;
            _pushSubscriptionService = pushSubscriptionService;
            _currentUser = currentUser;
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
        [Authorize]
        public async Task<IActionResult> Subscribe(
            [FromBody] PushSubscriptionRequest request,
            CancellationToken cancellationToken)
        {
            var actorUserId = _currentUser.UserId;
            if (!actorUserId.HasValue)
            {
                return Unauthorized();
            }

            if (string.IsNullOrWhiteSpace(request.Endpoint) ||
                string.IsNullOrWhiteSpace(request.Keys?.P256dh) ||
                string.IsNullOrWhiteSpace(request.Keys?.Auth))
            {
                return BadRequest(new { message = "Invalid push subscription payload." });
            }

            var result = await _pushSubscriptionService.Subscribe(
                actorUserId.Value,
                new PushSubscriptionInput(
                    request.Endpoint,
                    request.Keys.P256dh,
                    request.Keys.Auth,
                    request.UserAgent,
                    request.Platform,
                    request.DeviceName),
                cancellationToken);

            if (result == PushSubscriptionResult.Conflict)
            {
                return Conflict(new { message = "Push subscription endpoint is already registered with different keys." });
            }

            return Ok(new { success = true });
        }

        [HttpPost("unsubscribe")]
        [Authorize]
        public async Task<IActionResult> Unsubscribe(
            [FromBody] PushUnsubscribeRequest request,
            CancellationToken cancellationToken)
        {
            var actorUserId = _currentUser.UserId;
            if (!actorUserId.HasValue)
            {
                return Unauthorized();
            }

            if (string.IsNullOrWhiteSpace(request.Endpoint))
            {
                return BadRequest(new { message = "Endpoint is required." });
            }

            await _pushSubscriptionService.Unsubscribe(
                actorUserId.Value,
                request.Endpoint,
                cancellationToken);

            return Ok(new { success = true });
        }

        [Authorize]
        [HttpPost("broadcast")]
        public async Task<IActionResult> Broadcast([FromBody] PushBroadcastRequest request, CancellationToken cancellationToken)
        {
            var actorUserId = _currentUser.UserId;
            if (!actorUserId.HasValue)
            {
                return Unauthorized();
            }

            var isSuperAdmin = await _context.Users
                .AsNoTracking()
                .AnyAsync(
                    user => user.Id == actorUserId.Value && user.AppRole == AppRole.SuperAdmin,
                    cancellationToken);
            if (!isSuperAdmin)
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
