using HockeyPlanner.Backend.Application.Abstractions.Services;
using HockeyPlanner.Backend.Core.Entities;
using HockeyPlanner.Backend.Core.Enums;
using HockeyPlanner.Backend.Core.Exceptions;
using HockeyPlanner.Backend.Infrastructure.Data;
using HockeyPlanner.Backend.Shared.Models.Notifications;
using Microsoft.EntityFrameworkCore;

namespace HockeyPlanner.Backend.Application.Implementations.Services;

internal sealed class UserNotificationService : IUserNotificationService
{
    private readonly AppDbContext _context;
    private readonly TimeProvider _timeProvider;
    private readonly INotificationService _notificationService;

    public UserNotificationService(
        AppDbContext context,
        TimeProvider timeProvider,
        INotificationService notificationService)
    {
        _context = context;
        _timeProvider = timeProvider;
        _notificationService = notificationService;
    }

    public async Task<NotificationsListDto> GetInbox(
        Guid actorUserId,
        int skip,
        int take,
        CancellationToken cancellationToken)
    {
        var query = _context.Notifications
            .AsNoTracking()
            .Where(notification => notification.UserId == actorUserId);

        var unreadCount = await query.CountAsync(notification => !notification.IsRead, cancellationToken);
        var items = await query
            .OrderBy(notification => notification.IsRead)
            .ThenByDescending(notification => notification.CreatedAt)
            .Skip(skip)
            .Take(take)
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
                DeliveredAt = notification.DeliveredAt,
            })
            .ToListAsync(cancellationToken);

        return new NotificationsListDto { Items = items, UnreadCount = unreadCount };
    }

    public async Task MarkRead(
        Guid actorUserId,
        Guid notificationId,
        CancellationToken cancellationToken)
    {
        var notification = await _context.Notifications
            .SingleOrDefaultAsync(
                value => value.Id == notificationId && value.UserId == actorUserId,
                cancellationToken);

        if (notification is null)
        {
            throw new NotFoundException("Уведомление не найдено.");
        }

        if (notification.IsRead)
        {
            return;
        }

        var now = _timeProvider.GetUtcNow().UtcDateTime;
        notification.IsRead = true;
        notification.ReadAt = now;
        notification.UpdatedAt = now;
        await _context.SaveChangesAsync(cancellationToken);
    }

    public async Task MarkAllRead(
        Guid actorUserId,
        CancellationToken cancellationToken)
    {
        var now = _timeProvider.GetUtcNow().UtcDateTime;
        await _context.Notifications
            .Where(notification => notification.UserId == actorUserId && !notification.IsRead)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(notification => notification.IsRead, true)
                .SetProperty(notification => notification.ReadAt, now)
                .SetProperty(notification => notification.UpdatedAt, now), cancellationToken);
    }

    public async Task<NotificationPreferencesDto> GetPreferences(
        Guid actorUserId,
        CancellationToken cancellationToken)
    {
        var preferences = await GetOrCreatePreferences(actorUserId, cancellationToken);
        return ToDto(preferences);
    }

    public async Task<NotificationPreferencesDto> UpdatePreferences(
        Guid actorUserId,
        NotificationPreferencesDto request,
        CancellationToken cancellationToken)
    {
        var preferences = await GetOrCreatePreferences(actorUserId, cancellationToken);
        preferences.AttendanceRequiredEnabled = request.AttendanceRequiredEnabled;
        preferences.RosterReadyEnabled = request.RosterReadyEnabled;
        preferences.TeamNewsEnabled = request.TeamNewsEnabled;
        preferences.GoaliesEnabled = request.GoaliesEnabled;
        preferences.BirthdaysEnabled = request.BirthdaysEnabled;
        preferences.AppUpdatesEnabled = request.AppUpdatesEnabled;
        preferences.UpdatedAt = _timeProvider.GetUtcNow().UtcDateTime;

        await _context.SaveChangesAsync(cancellationToken);
        return ToDto(preferences);
    }

    public async Task SendSelfTestNotification(
        Guid actorUserId,
        CancellationToken cancellationToken)
    {
        var userExists = await _context.Users
            .AsNoTracking()
            .AnyAsync(user => user.Id == actorUserId, cancellationToken);
        if (!userExists)
        {
            throw new NotFoundException("Пользователь не найден.");
        }

        await _notificationService.NotifyUserAsync(
            actorUserId,
            NotificationType.AppUpdatePublished,
            NotificationCategory.AppUpdates,
            "Тестовое уведомление",
            "Notification Center работает.",
            "/settings",
            cancellationToken);
    }

    private async Task<NotificationPreferences> GetOrCreatePreferences(
        Guid actorUserId,
        CancellationToken cancellationToken)
    {
        var preferences = await _context.NotificationPreferences
            .SingleOrDefaultAsync(value => value.UserId == actorUserId, cancellationToken);
        if (preferences is not null)
        {
            return preferences;
        }

        var userExists = await _context.Users
            .AsNoTracking()
            .AnyAsync(user => user.Id == actorUserId, cancellationToken);
        if (!userExists)
        {
            throw new NotFoundException("Пользователь не найден.");
        }

        var now = _timeProvider.GetUtcNow().UtcDateTime;
        preferences = new NotificationPreferences
        {
            UserId = actorUserId,
            CreatedAt = now,
            UpdatedAt = now,
        };
        await _context.NotificationPreferences.AddAsync(preferences, cancellationToken);
        await _context.SaveChangesAsync(cancellationToken);

        return preferences;
    }

    private static NotificationPreferencesDto ToDto(NotificationPreferences preferences) =>
        new()
        {
            AttendanceRequiredEnabled = preferences.AttendanceRequiredEnabled,
            RosterReadyEnabled = preferences.RosterReadyEnabled,
            TeamNewsEnabled = preferences.TeamNewsEnabled,
            GoaliesEnabled = preferences.GoaliesEnabled,
            BirthdaysEnabled = preferences.BirthdaysEnabled,
            AppUpdatesEnabled = preferences.AppUpdatesEnabled,
        };
}
