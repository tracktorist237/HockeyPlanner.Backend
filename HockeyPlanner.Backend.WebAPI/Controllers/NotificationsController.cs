using HockeyPlanner.Backend.Application.Abstractions.Services;
using HockeyPlanner.Backend.Core.Entities;
using HockeyPlanner.Backend.Core.Enums;
using HockeyPlanner.Backend.Infrastructure.Data;
using HockeyPlanner.Backend.WebAPI.Models.Notifications;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace HockeyPlanner.Backend.WebAPI.Controllers
{
    [ApiController]
    [Route("api/notifications")]
    public class NotificationsController : ControllerBase
    {
        private readonly AppDbContext _context;
        private readonly INotificationService _notificationService;

        public NotificationsController(AppDbContext context, INotificationService notificationService)
        {
            _context = context;
            _notificationService = notificationService;
        }

        [HttpGet]
        public async Task<ActionResult<NotificationsListDto>> GetNotifications(
            [FromQuery] Guid currentUserId,
            [FromQuery] int skip = 0,
            [FromQuery] int take = 20,
            CancellationToken cancellationToken = default)
        {
            if (currentUserId == Guid.Empty)
            {
                return BadRequest(new { message = "Параметр currentUserId обязателен." });
            }

            var safeTake = Math.Clamp(take, 1, 50);
            var safeSkip = Math.Max(skip, 0);

            var query = _context.Notifications
                .AsNoTracking()
                .Where(notification => notification.UserId == currentUserId);

            var unreadCount = await query.CountAsync(notification => !notification.IsRead, cancellationToken);
            var items = await query
                .OrderBy(notification => notification.IsRead)
                .ThenByDescending(notification => notification.CreatedAt)
                .Skip(safeSkip)
                .Take(safeTake)
                .Select(notification => new NotificationDto
                {
                    Id = notification.Id,
                    Type = notification.Type,
                    Category = notification.Category,
                    Title = notification.Title,
                    Body = notification.Body,
                    Url = notification.Url,
                    IsRead = notification.IsRead,
                    CreatedAt = notification.CreatedAt,
                    ReadAt = notification.ReadAt,
                    DeliveredAt = notification.DeliveredAt
                })
                .ToListAsync(cancellationToken);

            return Ok(new NotificationsListDto { Items = items, UnreadCount = unreadCount });
        }

        [HttpPost("{id:guid}/read")]
        public async Task<IActionResult> MarkRead(Guid id, [FromQuery] Guid currentUserId, CancellationToken cancellationToken)
        {
            var notification = await _context.Notifications
                .FirstOrDefaultAsync(value => value.Id == id && value.UserId == currentUserId, cancellationToken);

            if (notification == null)
            {
                return NotFound(new { message = "Уведомление не найдено." });
            }

            if (!notification.IsRead)
            {
                notification.IsRead = true;
                notification.ReadAt = DateTime.UtcNow;
                notification.UpdatedAt = DateTime.UtcNow;
                await _context.SaveChangesAsync(cancellationToken);
            }

            return Ok();
        }

        [HttpPost("read-all")]
        public async Task<IActionResult> MarkAllRead([FromQuery] Guid currentUserId, CancellationToken cancellationToken)
        {
            if (currentUserId == Guid.Empty)
            {
                return BadRequest(new { message = "Параметр currentUserId обязателен." });
            }

            var now = DateTime.UtcNow;
            await _context.Notifications
                .Where(notification => notification.UserId == currentUserId && !notification.IsRead)
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(notification => notification.IsRead, true)
                    .SetProperty(notification => notification.ReadAt, now)
                    .SetProperty(notification => notification.UpdatedAt, now), cancellationToken);

            return Ok();
        }

        [HttpGet("preferences/me")]
        [HttpGet("/api/notification-preferences/me")]
        public async Task<ActionResult<NotificationPreferencesDto>> GetPreferences(
            [FromQuery] Guid currentUserId,
            CancellationToken cancellationToken)
        {
            if (currentUserId == Guid.Empty)
            {
                return BadRequest(new { message = "Параметр currentUserId обязателен." });
            }

            var preferences = await EnsurePreferences(currentUserId, cancellationToken);
            return Ok(ToDto(preferences));
        }

        [HttpPut("preferences/me")]
        [HttpPut("/api/notification-preferences/me")]
        public async Task<IActionResult> UpdatePreferences(
            [FromQuery] Guid currentUserId,
            [FromBody] NotificationPreferencesDto request,
            CancellationToken cancellationToken)
        {
            if (currentUserId == Guid.Empty)
            {
                return BadRequest(new { message = "Параметр currentUserId обязателен." });
            }

            var preferences = await EnsurePreferences(currentUserId, cancellationToken);
            preferences.AttendanceRequiredEnabled = request.AttendanceRequiredEnabled;
            preferences.RosterReadyEnabled = request.RosterReadyEnabled;
            preferences.TeamNewsEnabled = request.TeamNewsEnabled;
            preferences.GoaliesEnabled = request.GoaliesEnabled;
            preferences.BirthdaysEnabled = request.BirthdaysEnabled;
            preferences.AppUpdatesEnabled = request.AppUpdatesEnabled;
            preferences.UpdatedAt = DateTime.UtcNow;

            await _context.SaveChangesAsync(cancellationToken);
            return Ok(ToDto(preferences));
        }

        [HttpPost("test")]
        public async Task<IActionResult> SendTest([FromQuery] Guid currentUserId, CancellationToken cancellationToken)
        {
            if (currentUserId == Guid.Empty)
            {
                return BadRequest(new { message = "Параметр currentUserId обязателен." });
            }

            await _notificationService.NotifyUserAsync(
                currentUserId,
                NotificationType.AppUpdatePublished,
                NotificationCategory.AppUpdates,
                "Тестовое уведомление",
                "Notification Center работает.",
                "/settings",
                cancellationToken);

            return Ok();
        }

        private async Task<NotificationPreferences> EnsurePreferences(Guid userId, CancellationToken cancellationToken)
        {
            var preferences = await _context.NotificationPreferences
                .FirstOrDefaultAsync(value => value.UserId == userId, cancellationToken);

            if (preferences != null)
            {
                return preferences;
            }

            var userExists = await _context.Users.AnyAsync(user => user.Id == userId, cancellationToken);
            if (!userExists)
            {
                throw new InvalidOperationException("Пользователь не найден.");
            }

            preferences = new NotificationPreferences
            {
                UserId = userId,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            };
            await _context.NotificationPreferences.AddAsync(preferences, cancellationToken);
            await _context.SaveChangesAsync(cancellationToken);

            return preferences;
        }

        private static NotificationPreferencesDto ToDto(NotificationPreferences preferences)
        {
            return new NotificationPreferencesDto
            {
                AttendanceRequiredEnabled = preferences.AttendanceRequiredEnabled,
                RosterReadyEnabled = preferences.RosterReadyEnabled,
                TeamNewsEnabled = preferences.TeamNewsEnabled,
                GoaliesEnabled = preferences.GoaliesEnabled,
                BirthdaysEnabled = preferences.BirthdaysEnabled,
                AppUpdatesEnabled = preferences.AppUpdatesEnabled
            };
        }
    }
}
