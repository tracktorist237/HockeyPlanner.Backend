using HockeyPlanner.Backend.Application.Abstractions.Services;
using HockeyPlanner.Backend.Core.Entities;
using HockeyPlanner.Backend.Core.Enums;
using HockeyPlanner.Backend.Core.Exceptions;
using HockeyPlanner.Backend.Infrastructure.Data;
using HockeyPlanner.Backend.WebAPI.Models.Events;
using Microsoft.EntityFrameworkCore;

namespace HockeyPlanner.Backend.WebAPI.Services;

public interface IEventDataTransferService
{
    Task<AttendanceTransferPreviewDto> PreviewAttendanceAsync(Guid sourceEventId, Guid actorUserId, PreviewAttendanceTransferRequest request, CancellationToken cancellationToken);
    Task TransferAsync(Guid sourceEventId, Guid actorUserId, TransferEventDataRequest request, CancellationToken cancellationToken);
}

public sealed class EventDataTransferService(AppDbContext context, INotificationService notifications) : IEventDataTransferService
{
    public async Task<AttendanceTransferPreviewDto> PreviewAttendanceAsync(
        Guid sourceEventId,
        Guid actorUserId,
        PreviewAttendanceTransferRequest request,
        CancellationToken cancellationToken)
    {
        ValidateMode(request.AttendanceTransferMode);
        var (source, target) = await LoadAndAuthorizeAsync(sourceEventId, request.TargetEventId, actorUserId, cancellationToken);
        return BuildAttendancePreview(source, target, request.AttendanceTransferMode);
    }

    public async Task TransferAsync(Guid sourceEventId, Guid actorUserId, TransferEventDataRequest request, CancellationToken cancellationToken)
    {
        ValidateMode(request.AttendanceTransferMode);

        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);
        var (source, target) = await LoadAndAuthorizeAsync(sourceEventId, request.TargetEventId, actorUserId, cancellationToken);

        var sourceRoster = request.Roster
            ? await context.Lines.AsNoTracking().Include(line => line.Players)
                .Where(line => line.EventId == source.Id).OrderBy(line => line.Order).ToListAsync(cancellationToken)
            : [];
        var targetLineIds = request.Roster
            ? await context.Lines.AsNoTracking().Where(line => line.EventId == target.Id)
                .Select(line => line.Id).ToListAsync(cancellationToken)
            : [];

        var confirmedAttendanceUserIds = request.Attendance
            ? MergeAttendance(context, source, target, request.AttendanceTransferMode)
            : [];
        var rosterGuestIds = request.Roster
            ? sourceRoster.SelectMany(line => line.Players).Where(player => player.EventGuestId.HasValue)
                .Select(player => player.EventGuestId!.Value).ToHashSet()
            : [];
        var guestMap = request.Guests || rosterGuestIds.Count > 0
            ? MergeGuests(context, source, target, request.Guests, rosterGuestIds)
            : new Dictionary<Guid, Guid>();
        if (request.Roster) await ReplaceRosterAsync(context, sourceRoster, target, targetLineIds, guestMap, cancellationToken);
        if (request.UniformColor) target.UniformColorId = source.UniformColorId;
        if (request.Description) target.Description = source.Description;
        await context.SaveChangesAsync(cancellationToken);
        if (request.DeleteSourceEvent)
            await context.Events.Where(value => value.Id == source.Id).ExecuteDeleteAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        if (confirmedAttendanceUserIds.Count > 0)
        {
            await notifications.NotifyUsersAsync(
                confirmedAttendanceUserIds,
                NotificationType.EventPublished,
                NotificationCategory.AttendanceRequired,
                "Явка перенесена",
                $"Ваша отметка «Смогу» перенесена в мероприятие «{target.Title}».",
                $"/events/{target.Id}",
                cancellationToken);
        }
    }

    private async Task<(ScheduledEvent Source, ScheduledEvent Target)> LoadAndAuthorizeAsync(
        Guid sourceEventId,
        Guid targetEventId,
        Guid actorUserId,
        CancellationToken cancellationToken)
    {
        if (sourceEventId == targetEventId)
            throw new BusinessRuleException("Исходное и целевое мероприятия должны различаться.");

        var events = await context.Events
            .Include(value => value.Attendances).ThenInclude(value => value.User)
            .Include(value => value.EventGuests)
            .Where(value => value.Id == sourceEventId || value.Id == targetEventId)
            .ToListAsync(cancellationToken);
        var source = events.SingleOrDefault(value => value.Id == sourceEventId)
            ?? throw new NotFoundException("Исходное мероприятие не найдено");
        var target = events.SingleOrDefault(value => value.Id == targetEventId)
            ?? throw new NotFoundException("Целевое мероприятие не найдено");
        if (!source.TeamId.HasValue || source.TeamId != target.TeamId)
            throw new BusinessRuleException("Мероприятия должны принадлежать одной команде.");
        var canManage = await context.TeamMemberships.AsNoTracking().AnyAsync(value =>
            value.TeamId == source.TeamId && value.UserId == actorUserId &&
            (value.Role == TeamMemberRole.Owner || value.Role == TeamMemberRole.Admin), cancellationToken);
        if (!canManage) throw new UnauthorizedException("Недостаточно прав для переноса данных мероприятий.");
        return (source, target);
    }

    private static AttendanceTransferPreviewDto BuildAttendancePreview(
        ScheduledEvent source,
        ScheduledEvent target,
        AttendanceTransferMode mode)
    {
        var targetByUser = target.Attendances.ToDictionary(value => value.UserId);
        return new AttendanceTransferPreviewDto
        {
            Items = source.Attendances.Select(item =>
            {
                var targetStatus = targetByUser.TryGetValue(item.UserId, out var current) ? current.Status : (AttendanceStatus?)null;
                var resultingStatus = ResolveAttendanceStatus(item.Status, targetStatus, mode);
                return new AttendanceTransferPreviewItemDto
                {
                    UserId = item.UserId,
                    UserDisplayName = string.IsNullOrWhiteSpace(item.User.FullName) ? null : item.User.FullName,
                    SourceStatus = item.Status,
                    TargetStatus = targetStatus,
                    ResultingStatus = resultingStatus,
                    WillChange = resultingStatus.HasValue && resultingStatus != targetStatus
                };
            }).OrderBy(value => value.UserDisplayName).ThenBy(value => value.UserId).ToArray()
        };
    }

    private static IReadOnlyCollection<Guid> MergeAttendance(
        AppDbContext context,
        ScheduledEvent source,
        ScheduledEvent target,
        AttendanceTransferMode mode)
    {
        var targetByUser = target.Attendances.ToDictionary(value => value.UserId);
        var confirmedUserIds = new HashSet<Guid>();
        foreach (var item in source.Attendances)
        {
            targetByUser.TryGetValue(item.UserId, out var current);
            var resultingStatus = ResolveAttendanceStatus(item.Status, current?.Status, mode);
            if (!resultingStatus.HasValue || resultingStatus == current?.Status) continue;

            if (current is null)
            {
                var copy = new Attendance { EventId = target.Id, UserId = item.UserId, Status = resultingStatus.Value, Notes = item.Notes, RespondedAt = item.RespondedAt };
                context.Attendances.Add(copy);
                targetByUser[item.UserId] = copy;
            }
            else
            {
                current.Status = resultingStatus.Value;
                current.Notes = item.Notes;
                current.RespondedAt = item.RespondedAt;
            }
            if (resultingStatus == AttendanceStatus.Confirmed) confirmedUserIds.Add(item.UserId);
        }

        return confirmedUserIds;
    }

    private static AttendanceStatus? ResolveAttendanceStatus(
        AttendanceStatus sourceStatus,
        AttendanceStatus? targetStatus,
        AttendanceTransferMode mode) => mode switch
        {
            AttendanceTransferMode.ReplaceTarget => sourceStatus != AttendanceStatus.Pending || targetStatus is null or AttendanceStatus.Pending
                ? sourceStatus
                : targetStatus,
            AttendanceTransferMode.MergePreferTarget => targetStatus is not null and not AttendanceStatus.Pending
                ? targetStatus
                : sourceStatus,
            AttendanceTransferMode.ConfirmedOnly => sourceStatus == AttendanceStatus.Confirmed
                ? AttendanceStatus.Confirmed
                : targetStatus,
            _ => throw new BusinessRuleException("Неизвестный режим переноса явки.")
        };

    private static void ValidateMode(AttendanceTransferMode mode)
    {
        if (!Enum.IsDefined(mode)) throw new BusinessRuleException("Неизвестный режим переноса явки.");
    }

    private static Dictionary<Guid, Guid> MergeGuests(
        AppDbContext context,
        ScheduledEvent source,
        ScheduledEvent target,
        bool includeAll,
        IReadOnlySet<Guid> rosterGuestIds)
    {
        static string Key(EventGuest value) => $"{value.FirstName.Trim()}|{value.LastName.Trim()}".ToUpperInvariant();
        var targetByKey = target.EventGuests.GroupBy(Key).ToDictionary(group => group.Key, group => group.First());
        var map = new Dictionary<Guid, Guid>();
        foreach (var guest in source.EventGuests)
        {
            if (!includeAll && !rosterGuestIds.Contains(guest.Id)) continue;

            if (!targetByKey.TryGetValue(Key(guest), out var current))
            {
                current = new EventGuest
                {
                    EventId = target.Id, InvitedByUserId = guest.InvitedByUserId, FirstName = guest.FirstName,
                    LastName = guest.LastName, Handedness = guest.Handedness, JerseyNumber = guest.JerseyNumber,
                    Status = guest.Status, RespondedAt = guest.RespondedAt, Notes = guest.Notes
                };
                context.EventGuests.Add(current);
                targetByKey[Key(guest)] = current;
            }
            map[guest.Id] = current.Id;
        }
        return map;
    }

    private static async Task ReplaceRosterAsync(
        AppDbContext context,
        IReadOnlyCollection<Line> sourceRoster,
        ScheduledEvent target,
        IReadOnlyCollection<Guid> targetLineIds,
        IReadOnlyDictionary<Guid, Guid> guestMap,
        CancellationToken cancellationToken)
    {
        if (targetLineIds.Count > 0)
        {
            await context.Players.Where(player => targetLineIds.Contains(player.LineId))
                .ExecuteDeleteAsync(cancellationToken);
            await context.Lines.Where(line => targetLineIds.Contains(line.Id))
                .ExecuteDeleteAsync(cancellationToken);
        }

        foreach (var line in sourceRoster)
        {
            var copy = new Line { EventId = target.Id, Name = line.Name, Order = line.Order, UniformColorId = line.UniformColorId };
            foreach (var player in line.Players)
            {
                copy.Players.Add(new Player
                {
                    LineId = copy.Id, UserId = player.UserId,
                    EventGuestId = player.EventGuestId.HasValue ? guestMap[player.EventGuestId.Value] : null,
                    FirstName = player.FirstName, LastName = player.LastName, Role = player.Role,
                    JerseyNumber = player.JerseyNumber, Handedness = player.Handedness
                });
            }
            context.Lines.Add(copy);
        }
    }
}
