using HockeyPlanner.Backend.Application.Abstractions.Identity;
using HockeyPlanner.Backend.Application.Abstractions.Services;
using HockeyPlanner.Backend.Core.Exceptions;
using HockeyPlanner.Backend.Shared.Models.Notifications;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace HockeyPlanner.Backend.WebAPI.Controllers
{
    [ApiController]
    [Authorize]
    [Route("api/notifications")]
    public class NotificationsController : ControllerBase
    {
        private readonly IUserNotificationService _userNotificationService;
        private readonly ICurrentUser _currentUser;

        public NotificationsController(
            IUserNotificationService userNotificationService,
            ICurrentUser currentUser)
        {
            _userNotificationService = userNotificationService;
            _currentUser = currentUser;
        }

        [HttpGet]
        public async Task<ActionResult<NotificationsListDto>> GetNotifications(
            [FromQuery] int skip = 0,
            [FromQuery] int take = 20,
            CancellationToken cancellationToken = default)
        {
            var actorUserId = _currentUser.UserId;
            if (!actorUserId.HasValue)
            {
                return Unauthorized();
            }

            var safeTake = Math.Clamp(take, 1, 50);
            var safeSkip = Math.Max(skip, 0);

            return Ok(await _userNotificationService.GetInbox(
                actorUserId.Value,
                safeSkip,
                safeTake,
                cancellationToken));
        }

        [HttpPost("{id:guid}/read")]
        public async Task<IActionResult> MarkRead(
            Guid id,
            CancellationToken cancellationToken)
        {
            var actorUserId = _currentUser.UserId;
            if (!actorUserId.HasValue)
            {
                return Unauthorized();
            }

            try
            {
                await _userNotificationService.MarkRead(
                    actorUserId.Value,
                    id,
                    cancellationToken);
                return Ok();
            }
            catch (NotFoundException ex)
            {
                return NotFound(new { message = ex.Message });
            }
        }

        [HttpPost("read-all")]
        public async Task<IActionResult> MarkAllRead(CancellationToken cancellationToken)
        {
            var actorUserId = _currentUser.UserId;
            if (!actorUserId.HasValue)
            {
                return Unauthorized();
            }

            await _userNotificationService.MarkAllRead(
                actorUserId.Value,
                cancellationToken);
            return Ok();
        }

        [HttpGet("preferences/me")]
        [HttpGet("/api/notification-preferences/me")]
        public async Task<ActionResult<NotificationPreferencesDto>> GetPreferences(
            CancellationToken cancellationToken)
        {
            var actorUserId = _currentUser.UserId;
            if (!actorUserId.HasValue)
            {
                return Unauthorized();
            }

            try
            {
                return Ok(await _userNotificationService.GetPreferences(
                    actorUserId.Value,
                    cancellationToken));
            }
            catch (NotFoundException ex)
            {
                return NotFound(new { message = ex.Message });
            }
        }

        [HttpPut("preferences/me")]
        [HttpPut("/api/notification-preferences/me")]
        public async Task<ActionResult<NotificationPreferencesDto>> UpdatePreferences(
            [FromBody] NotificationPreferencesDto request,
            CancellationToken cancellationToken)
        {
            var actorUserId = _currentUser.UserId;
            if (!actorUserId.HasValue)
            {
                return Unauthorized();
            }

            try
            {
                return Ok(await _userNotificationService.UpdatePreferences(
                    actorUserId.Value,
                    request,
                    cancellationToken));
            }
            catch (NotFoundException ex)
            {
                return NotFound(new { message = ex.Message });
            }
        }

        [HttpPost("test")]
        public async Task<IActionResult> SendTest(CancellationToken cancellationToken)
        {
            var actorUserId = _currentUser.UserId;
            if (!actorUserId.HasValue)
            {
                return Unauthorized();
            }

            try
            {
                await _userNotificationService.SendSelfTestNotification(
                    actorUserId.Value,
                    cancellationToken);
                return Ok();
            }
            catch (NotFoundException ex)
            {
                return NotFound(new { message = ex.Message });
            }
        }
    }
}
