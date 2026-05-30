using HockeyPlanner.Backend.Application.Abstractions.Services;
using HockeyPlanner.Backend.Core.Entities;
using HockeyPlanner.Backend.Core.Enums;
using HockeyPlanner.Backend.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using System.Security.Cryptography;
using System.Text;

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
                .ToHashSet();

            var notifications = distinctUserIds
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

            var deliveries = new List<NotificationDelivery>();
            var notificationByUserId = notifications.ToDictionary(value => value.UserId);
            var disabledUserIds = distinctUserIds.Where(userId => !enabledUserIds.Contains(userId)).ToList();

            foreach (var userId in disabledUserIds)
            {
                deliveries.Add(CreateSkippedDelivery(
                    notificationByUserId[userId],
                    "Push notifications disabled by user preferences.",
                    now));
            }

            if (enabledUserIds.Count == 0)
            {
                await SaveDeliveriesAsync(deliveries, cancellationToken);
                return;
            }

            if (!_webPushService.IsConfigured)
            {
                foreach (var userId in enabledUserIds)
                {
                    deliveries.Add(CreateSkippedDelivery(
                        notificationByUserId[userId],
                        "Web Push is not configured.",
                        now));
                }

                await SaveDeliveriesAsync(deliveries, cancellationToken);
                return;
            }

            var subscriptions = await _context.PushSubscriptions
                .Where(subscription =>
                    subscription.IsActive &&
                    subscription.UserId.HasValue &&
                    enabledUserIds.Contains(subscription.UserId.Value))
                .ToListAsync(cancellationToken);

            var subscriptionsByUserId = subscriptions
                .Where(subscription => subscription.UserId.HasValue)
                .GroupBy(subscription => subscription.UserId!.Value)
                .ToDictionary(group => group.Key, group => group.ToList());

            foreach (var userId in enabledUserIds)
            {
                if (!subscriptionsByUserId.ContainsKey(userId))
                {
                    deliveries.Add(CreateSkippedDelivery(
                        notificationByUserId[userId],
                        "No active push subscriptions.",
                        now));
                }
            }

            var deliveredUserIds = new HashSet<Guid>();
            foreach (var subscription in subscriptions)
            {
                if (!subscription.UserId.HasValue || !notificationByUserId.TryGetValue(subscription.UserId.Value, out var notification))
                {
                    continue;
                }

                var delivery = new NotificationDelivery
                {
                    NotificationId = notification.Id,
                    UserId = subscription.UserId.Value,
                    PushSubscriptionId = subscription.Id,
                    Status = NotificationDeliveryStatus.Pending,
                    EndpointHash = HashEndpoint(subscription.Endpoint),
                    CreatedAt = now,
                    UpdatedAt = now
                };

                try
                {
                    var result = await _webPushService.SendAsync(subscription, new { title, body, url }, cancellationToken);
                    if (result.IsSuccess)
                    {
                        delivery.Status = NotificationDeliveryStatus.Sent;
                        delivery.SentAt = DateTime.UtcNow;
                        delivery.UpdatedAt = delivery.SentAt.Value;
                        deliveredUserIds.Add(subscription.UserId.Value);
                        deliveries.Add(delivery);
                        continue;
                    }

                    if (result.ShouldRemoveSubscription)
                    {
                        subscription.IsActive = false;
                        subscription.RevokedAt = now;
                        subscription.UpdatedAt = now;
                        delivery.Status = NotificationDeliveryStatus.EndpointInactive;
                    }
                    else
                    {
                        delivery.Status = NotificationDeliveryStatus.Failed;
                    }

                    delivery.Error = TruncateError(result.Error);
                    delivery.UpdatedAt = DateTime.UtcNow;
                    deliveries.Add(delivery);
                }
                catch (Exception exception)
                {
                    delivery.Status = NotificationDeliveryStatus.Failed;
                    delivery.Error = TruncateError(exception.Message);
                    delivery.UpdatedAt = DateTime.UtcNow;
                    deliveries.Add(delivery);
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

        private async Task SaveDeliveriesAsync(List<NotificationDelivery> deliveries, CancellationToken cancellationToken)
        {
            if (deliveries.Count == 0)
            {
                await _context.SaveChangesAsync(cancellationToken);
                return;
            }

            await _context.NotificationDeliveries.AddRangeAsync(deliveries, cancellationToken);
            await _context.SaveChangesAsync(cancellationToken);
        }

        private static NotificationDelivery CreateSkippedDelivery(Notification notification, string reason, DateTime now)
        {
            return new NotificationDelivery
            {
                NotificationId = notification.Id,
                UserId = notification.UserId,
                Status = NotificationDeliveryStatus.Skipped,
                Error = reason,
                CreatedAt = now,
                UpdatedAt = now
            };
        }

        private static string? HashEndpoint(string? endpoint)
        {
            if (string.IsNullOrWhiteSpace(endpoint))
            {
                return null;
            }

            var hashBytes = SHA256.HashData(Encoding.UTF8.GetBytes(endpoint));
            return Convert.ToHexString(hashBytes).ToLowerInvariant();
        }

        private static string? TruncateError(string? error)
        {
            if (string.IsNullOrWhiteSpace(error))
            {
                return null;
            }

            var trimmed = error.Trim();
            return trimmed.Length <= 1000 ? trimmed : trimmed[..1000];
        }
    }
}
