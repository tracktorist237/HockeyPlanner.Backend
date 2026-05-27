using HockeyPlanner.Backend.Application.Abstractions.Services;
using HockeyPlanner.Backend.Core.Entities;
using HockeyPlanner.Backend.Core.Enums;
using HockeyPlanner.Backend.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace HockeyPlanner.Backend.WebAPI.Services
{
    public class NotificationService : INotificationService
    {
        private readonly AppDbContext _context;
        private readonly IWebPushService _webPushService;
        private readonly ILogger<NotificationService> _logger;

        public NotificationService(
            AppDbContext context,
            IWebPushService webPushService,
            ILogger<NotificationService> logger)
        {
            _context = context;
            _webPushService = webPushService;
            _logger = logger;
        }

        public Task NotifyUserAsync(
            Guid userId,
            NotificationType type,
            NotificationCategory category,
            string title,
            string body,
            string? url = null,
            CancellationToken cancellationToken = default)
        {
            return NotifyUsersAsync([userId], type, category, title, body, url, cancellationToken);
        }

        public async Task NotifyUsersAsync(
            IReadOnlyCollection<Guid> userIds,
            NotificationType type,
            NotificationCategory category,
            string title,
            string body,
            string? url = null,
            CancellationToken cancellationToken = default)
        {
            var distinctUserIds = userIds
                .Where(id => id != Guid.Empty)
                .Distinct()
                .ToList();

            if (distinctUserIds.Count == 0)
            {
                return;
            }

            var now = DateTime.UtcNow;
            var existingPreferenceUserIds = await _context.NotificationPreferences
                .Where(preferences => distinctUserIds.Contains(preferences.UserId))
                .Select(preferences => preferences.UserId)
                .ToListAsync(cancellationToken);

            var missingPreferenceUserIds = distinctUserIds.Except(existingPreferenceUserIds).ToList();
            var createdPreferences = missingPreferenceUserIds.Select(userId => new NotificationPreferences
            {
                UserId = userId,
                CreatedAt = now,
                UpdatedAt = now
            }).ToList();
            if (missingPreferenceUserIds.Count > 0)
            {
                await _context.NotificationPreferences.AddRangeAsync(createdPreferences, cancellationToken);
            }

            var preferences = await _context.NotificationPreferences
                .Where(value => distinctUserIds.Contains(value.UserId))
                .ToListAsync(cancellationToken);
            preferences.AddRange(createdPreferences);

            var enabledUserIds = preferences
                .Where(value => IsEnabled(value, category))
                .Select(value => value.UserId)
                .ToList();

            if (enabledUserIds.Count == 0)
            {
                await _context.SaveChangesAsync(cancellationToken);
                return;
            }

            var notifications = enabledUserIds
                .Select(userId => new Notification
                {
                    UserId = userId,
                    Type = type,
                    Category = category,
                    Title = title.Trim(),
                    Body = body.Trim(),
                    Url = string.IsNullOrWhiteSpace(url) ? null : url.Trim(),
                    IsRead = false,
                    CreatedAt = now,
                    UpdatedAt = now
                })
                .ToList();

            await _context.Notifications.AddRangeAsync(notifications, cancellationToken);
            await _context.SaveChangesAsync(cancellationToken);

            if (!_webPushService.IsConfigured)
            {
                return;
            }

            var subscriptions = await _context.PushSubscriptions
                .Where(subscription =>
                    subscription.IsActive &&
                    subscription.UserId.HasValue &&
                    enabledUserIds.Contains(subscription.UserId.Value))
                .ToListAsync(cancellationToken);

            if (subscriptions.Count == 0)
            {
                return;
            }

            var deliveredUserIds = new HashSet<Guid>();
            foreach (var subscription in subscriptions)
            {
                try
                {
                    var result = await _webPushService.SendAsync(subscription, new { title, body, url }, cancellationToken);
                    if (result.IsSuccess)
                    {
                        if (subscription.UserId.HasValue)
                        {
                            deliveredUserIds.Add(subscription.UserId.Value);
                        }
                        continue;
                    }

                    if (result.ShouldRemoveSubscription)
                    {
                        subscription.IsActive = false;
                        subscription.RevokedAt = now;
                        subscription.UpdatedAt = now;
                    }
                }
                catch (Exception exception)
                {
                    _logger.LogWarning(exception, "Notification push failed for subscription {SubscriptionId}", subscription.Id);
                }
            }

            if (deliveredUserIds.Count > 0)
            {
                foreach (var notification in notifications.Where(value => deliveredUserIds.Contains(value.UserId)))
                {
                    notification.DeliveredAt = now;
                    notification.UpdatedAt = now;
                }
            }

            await _context.SaveChangesAsync(cancellationToken);
        }

        public async Task NotifyTeamAsync(
            Guid teamId,
            NotificationType type,
            NotificationCategory category,
            string title,
            string body,
            string? url = null,
            CancellationToken cancellationToken = default)
        {
            var userIds = await _context.TeamMemberships
                .AsNoTracking()
                .Where(membership => membership.TeamId == teamId)
                .Select(membership => membership.UserId)
                .ToListAsync(cancellationToken);

            await NotifyUsersAsync(userIds, type, category, title, body, url, cancellationToken);
        }

        private static bool IsEnabled(NotificationPreferences preferences, NotificationCategory category)
        {
            return category switch
            {
                NotificationCategory.AttendanceRequired => preferences.AttendanceRequiredEnabled,
                NotificationCategory.RosterReady => preferences.RosterReadyEnabled,
                NotificationCategory.TeamNews => preferences.TeamNewsEnabled,
                NotificationCategory.Goalies => preferences.GoaliesEnabled,
                NotificationCategory.Birthdays => preferences.BirthdaysEnabled,
                NotificationCategory.AppUpdates => preferences.AppUpdatesEnabled,
                _ => true
            };
        }
    }
}
