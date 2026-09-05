using HockeyPlanner.Backend.Application.Abstractions.Services;
using HockeyPlanner.Backend.Core.Enums;
using HockeyPlanner.Backend.WebAPI.Models.ExternalLeagues;

namespace HockeyPlanner.Backend.WebAPI.Services;

public interface IExternalLeagueCreatedEventNotifier
{
    Task NotifyAsync(
        Guid teamId,
        IEnumerable<ExternalCreatedEvent> createdEvents,
        CancellationToken cancellationToken);
}

public sealed class ExternalLeagueCreatedEventNotifier(INotificationService notifications)
    : IExternalLeagueCreatedEventNotifier
{
    public async Task NotifyAsync(
        Guid teamId,
        IEnumerable<ExternalCreatedEvent> createdEvents,
        CancellationToken cancellationToken)
    {
        var events = createdEvents
            .Where(value => value.EventId != Guid.Empty)
            .GroupBy(value => value.EventId)
            .Select(group => group.First())
            .ToArray();
        if (events.Length == 0) return;

        var single = events.Length == 1;
        await notifications.NotifyTeamAsync(
            teamId,
            NotificationType.EventPublished,
            NotificationCategory.AttendanceRequired,
            single ? "Новое мероприятие" : "Новые мероприятия",
            single
                ? $"{events[0].Title}: отметьтесь, сможете ли быть."
                : $"Появилось {events.Length} новых мероприятий из лиги. Отметьтесь, сможете ли быть.",
            single ? $"/events/{events[0].EventId}" : "/events",
            cancellationToken);
    }
}
