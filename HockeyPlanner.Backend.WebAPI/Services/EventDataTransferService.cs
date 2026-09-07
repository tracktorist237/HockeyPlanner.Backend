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

public sealed class EventDataTransferService(AppDbContext context, INotificationService notifications, ILogger<EventDataTransferService>? logger = null) : IEventDataTransferService
{
    public async Task<AttendanceTransferPreviewDto> PreviewAttendanceAsync(
        Guid sourceEventId,
        Guid actorUserId,
        PreviewAttendanceTransferRequest request,
        CancellationToken cancellationToken)
    {
        ValidateMode(request.AttendanceTransferMode);
        var (source, target) = await LoadAndAuthorizeAsync(sourceEventId, request.TargetEventId, actorUserId, cancellationToken);
        var eligibleUserIds = await LoadEligibleUserIdsAsync(source.TeamId!.Value, cancellationToken);
        return BuildAttendancePreview(source, target, request.AttendanceTransferMode, eligibleUserIds);
    }

    public async Task TransferAsync(Guid sourceEventId, Guid actorUserId, TransferEventDataRequest request, CancellationToken cancellationToken)
    {
        ValidateMode(request.AttendanceTransferMode);

        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);
        var (source, target) = await LoadAndAuthorizeAsync(sourceEventId, request.TargetEventId, actorUserId, cancellationToken);
        if (request.DeleteSourceEvent && source.ExternalLeagueProvider.HasValue &&
            (string.IsNullOrWhiteSpace(source.ExternalCompetitionId) || string.IsNullOrWhiteSpace(source.ExternalMatchId)))
        {
            throw new BusinessRuleException("Нельзя удалить мероприятие лиги без полного внешнего идентификатора.");
        }

        var sourceRoster = request.Roster
            ? await context.Lines.AsNoTracking().Include(line => line.Players)
                .Where(line => line.EventId == source.Id).OrderBy(line => line.Order).ToListAsync(cancellationToken)
            : [];
        var targetLineIds = request.Roster
            ? await context.Lines.AsNoTracking().Where(line => line.EventId == target.Id)
                .Select(line => line.Id).ToListAsync(cancellationToken)
            : [];

        var eligibleUserIds = await LoadEligibleUserIdsAsync(source.TeamId!.Value, cancellationToken);
        var attendanceMerge = request.Attendance
            ? MergeAttendance(context, source, target, request.AttendanceTransferMode, request.AttendanceOverrides, eligibleUserIds)
            : null;
        var rosterGuestIds = request.Roster
            ? sourceRoster.SelectMany(line => line.Players).Where(player => player.EventGuestId.HasValue)
                .Select(player => player.EventGuestId!.Value).ToHashSet()
            : [];
        var guestMerge = request.Guests || rosterGuestIds.Count > 0
            ? MergeGuests(context, source, target, request.Guests, rosterGuestIds, request.Attendance, request.AttendanceTransferMode)
            : GuestMergeResult.Empty;
        if (request.Roster)
            await ReplaceRosterAsync(context, sourceRoster, target, targetLineIds, guestMerge.TargetGuestIds,
                request.Attendance ? attendanceMerge!.ResultingStatuses : null,
                request.Attendance ? guestMerge.ResultingStatuses : null,
                cancellationToken);
        if (request.UniformColor) target.UniformColorId = source.UniformColorId;
        if (request.Description) target.Description = source.Description;
        await context.SaveChangesAsync(cancellationToken);
        if (request.DeleteSourceEvent && source.ExternalLeagueProvider.HasValue &&
            !string.IsNullOrWhiteSpace(source.ExternalCompetitionId) && !string.IsNullOrWhiteSpace(source.ExternalMatchId))
        {
            var exists = await context.ExternalEventSuppressions.AnyAsync(value =>
                value.TeamId == source.TeamId && value.ExternalLeagueProvider == source.ExternalLeagueProvider &&
                value.ExternalCompetitionId == source.ExternalCompetitionId && value.ExternalMatchId == source.ExternalMatchId,
                cancellationToken);
            if (!exists)
            {
                context.ExternalEventSuppressions.Add(new ExternalEventSuppression
                {
                    TeamId = source.TeamId.Value,
                    ExternalLeagueProvider = source.ExternalLeagueProvider.Value,
                    ExternalCompetitionId = source.ExternalCompetitionId,
                    ExternalMatchId = source.ExternalMatchId,
                    CreatedByUserId = actorUserId,
                    ExternalTitle = source.Title,
                    StartTime = source.StartTime,
                    CompetitionName = source.ExternalTournamentName
                });
                await context.SaveChangesAsync(cancellationToken);
            }
        }
        if (request.DeleteSourceEvent)
            await context.Events.Where(value => value.Id == source.Id).ExecuteDeleteAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        if (attendanceMerge is { Notifications.Count: > 0 })
        {
            foreach (var change in attendanceMerge.Notifications)
            {
                try
                {
                    var body = change.PreviousStatus is AttendanceStatus.Confirmed or AttendanceStatus.Declined
                        ? $"Ваша отметка в мероприятии «{target.Title}» изменена: «{StatusName(change.PreviousStatus.Value)}» → «{StatusName(change.ResultingStatus)}»."
                        : $"Ваша отметка перенесена в мероприятие «{target.Title}»: «{StatusName(change.ResultingStatus)}».";
                    await notifications.NotifyUserAsync(change.UserId, NotificationType.EventPublished,
                        NotificationCategory.AttendanceRequired, "Явка перенесена", body, $"/events/{target.Id}", CancellationToken.None);
                }
                catch (Exception exception)
                {
                    logger?.LogError(exception, "Attendance transfer notification failed for SourceEventId {SourceEventId}, TargetEventId {TargetEventId}, UserId {UserId}", source.Id, target.Id, change.UserId);
                }
            }
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

        if (context.Database.CurrentTransaction is not null)
        {
            await context.Events
                .FromSqlInterpolated($"SELECT * FROM events WHERE id = {sourceEventId} OR id = {targetEventId} FOR UPDATE")
                .ToListAsync(cancellationToken);
        }

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
        AttendanceTransferMode mode,
        IReadOnlySet<Guid> eligibleUserIds)
    {
        var targetByUser = target.Attendances.ToDictionary(value => value.UserId);
        return new AttendanceTransferPreviewDto
        {
            Items = source.Attendances.Where(item => eligibleUserIds.Contains(item.UserId)).Select(item =>
            {
                var targetStatus = targetByUser.TryGetValue(item.UserId, out var current) ? current.Status : (AttendanceStatus?)null;
                var resultingStatus = ResolveAttendanceStatus(item.Status, targetStatus, mode);
                return new AttendanceTransferPreviewItemDto
                {
                    UserId = item.UserId,
                    UserDisplayName = string.IsNullOrWhiteSpace(item.User.FullName) ? null : item.User.FullName,
                    SourceStatus = item.Status,
                    TargetStatus = targetStatus,
                    AutomaticResultStatus = resultingStatus,
                    FinalResultStatus = resultingStatus,
                    WillChange = resultingStatus.HasValue && resultingStatus != targetStatus
                };
            }).OrderBy(value => value.UserDisplayName).ThenBy(value => value.UserId).ToArray()
        };
    }

    private static AttendanceMergeResult MergeAttendance(
        AppDbContext context,
        ScheduledEvent source,
        ScheduledEvent target,
        AttendanceTransferMode mode,
        IReadOnlyCollection<AttendanceTransferOverrideDto> overrides,
        IReadOnlySet<Guid> eligibleUserIds)
    {
        var targetByUser = target.Attendances.ToDictionary(value => value.UserId);
        var duplicateOverride = overrides.GroupBy(value => value.UserId).FirstOrDefault(group => group.Count() > 1);
        if (duplicateOverride is not null) throw new BusinessRuleException("Для участника передано несколько вариантов итоговой явки.");
        if (overrides.Any(value => !Enum.IsDefined(value.ResultingStatus))) throw new BusinessRuleException("Передан неизвестный статус явки.");
        var sourceItems = source.Attendances.Where(value => eligibleUserIds.Contains(value.UserId)).ToArray();
        var sourceUserIds = sourceItems.Select(value => value.UserId).ToHashSet();
        if (overrides.Any(value => !sourceUserIds.Contains(value.UserId))) throw new BusinessRuleException("Участник не входит в набор переноса явки.");
        var overrideByUser = overrides.ToDictionary(value => value.UserId, value => value.ResultingStatus);
        var notificationChanges = new List<AttendanceNotificationChange>();
        var resultingStatuses = targetByUser
            .Where(value => eligibleUserIds.Contains(value.Key))
            .ToDictionary(value => value.Key, value => value.Value.Status);
        foreach (var item in sourceItems)
        {
            targetByUser.TryGetValue(item.UserId, out var current);
            var previousStatus = current?.Status;
            var resultingStatus = overrideByUser.TryGetValue(item.UserId, out var overridden)
                ? overridden
                : ResolveAttendanceStatus(item.Status, current?.Status, mode);
            if (!resultingStatus.HasValue) continue;
            resultingStatuses[item.UserId] = resultingStatus.Value;
            if (resultingStatus == current?.Status) continue;

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
            if (resultingStatus is AttendanceStatus.Confirmed or AttendanceStatus.Declined)
                notificationChanges.Add(new AttendanceNotificationChange(item.UserId, previousStatus, resultingStatus.Value));
        }

        return new AttendanceMergeResult(resultingStatuses, notificationChanges);
    }

    private async Task<IReadOnlySet<Guid>> LoadEligibleUserIdsAsync(Guid teamId, CancellationToken cancellationToken) =>
        (await context.TeamMemberships.AsNoTracking().Where(value => value.TeamId == teamId)
            .Select(value => value.UserId).Distinct().ToArrayAsync(cancellationToken)).ToHashSet();

    private static string StatusName(AttendanceStatus status) => status switch
    {
        AttendanceStatus.Confirmed => "Смогу",
        AttendanceStatus.Declined => "Не смогу",
        _ => "Не ответил"
    };

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

    private static GuestMergeResult MergeGuests(
        AppDbContext context,
        ScheduledEvent source,
        ScheduledEvent target,
        bool includeAll,
        IReadOnlySet<Guid> rosterGuestIds,
        bool applyAttendance,
        AttendanceTransferMode attendanceMode)
    {
        static string Key(EventGuest value) => $"{value.FirstName.Trim()}|{value.LastName.Trim()}".ToUpperInvariant();
        var targetByKey = target.EventGuests.GroupBy(Key).ToDictionary(group => group.Key, group => group.First());
        var map = new Dictionary<Guid, Guid>();
        var resultingStatuses = new Dictionary<Guid, AttendanceStatus?>();
        foreach (var guest in source.EventGuests)
        {
            if (!includeAll && !rosterGuestIds.Contains(guest.Id)) continue;

            targetByKey.TryGetValue(Key(guest), out var current);
            var resultingStatus = applyAttendance
                ? ResolveAttendanceStatus(guest.Status, current?.Status, attendanceMode)
                : current?.Status ?? guest.Status;
            resultingStatuses[guest.Id] = resultingStatus;

            if (current is null && (includeAll || !applyAttendance || resultingStatus == AttendanceStatus.Confirmed))
            {
                current = new EventGuest
                {
                    EventId = target.Id, InvitedByUserId = guest.InvitedByUserId, FirstName = guest.FirstName,
                    LastName = guest.LastName, Handedness = guest.Handedness, JerseyNumber = guest.JerseyNumber,
                    Status = resultingStatus ?? guest.Status, RespondedAt = guest.RespondedAt, Notes = guest.Notes
                };
                context.EventGuests.Add(current);
                targetByKey[Key(guest)] = current;
            }
            else if (current is not null && applyAttendance && resultingStatus.HasValue && resultingStatus != current.Status)
            {
                current.Status = resultingStatus.Value;
                current.RespondedAt = guest.RespondedAt;
                current.Notes = guest.Notes;
            }
            if (current is not null) map[guest.Id] = current.Id;
        }
        return new GuestMergeResult(map, resultingStatuses);
    }

    private static async Task ReplaceRosterAsync(
        AppDbContext context,
        IReadOnlyCollection<Line> sourceRoster,
        ScheduledEvent target,
        IReadOnlyCollection<Guid> targetLineIds,
        IReadOnlyDictionary<Guid, Guid> guestMap,
        IReadOnlyDictionary<Guid, AttendanceStatus>? userAttendanceStatuses,
        IReadOnlyDictionary<Guid, AttendanceStatus?>? guestAttendanceStatuses,
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
                if (userAttendanceStatuses is not null && !ShouldCopyRosterPlayer(player, userAttendanceStatuses, guestAttendanceStatuses))
                    continue;
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

    private static bool ShouldCopyRosterPlayer(
        Player player,
        IReadOnlyDictionary<Guid, AttendanceStatus> userAttendanceStatuses,
        IReadOnlyDictionary<Guid, AttendanceStatus?>? guestAttendanceStatuses)
    {
        if (player.UserId.HasValue)
            return userAttendanceStatuses.TryGetValue(player.UserId.Value, out var status) && status == AttendanceStatus.Confirmed;
        if (player.EventGuestId.HasValue)
            return guestAttendanceStatuses is not null &&
                guestAttendanceStatuses.TryGetValue(player.EventGuestId.Value, out var status) &&
                status == AttendanceStatus.Confirmed;
        return false;
    }

    private sealed record AttendanceMergeResult(
        IReadOnlyDictionary<Guid, AttendanceStatus> ResultingStatuses,
        IReadOnlyCollection<AttendanceNotificationChange> Notifications);

    private sealed record AttendanceNotificationChange(Guid UserId, AttendanceStatus? PreviousStatus, AttendanceStatus ResultingStatus);

    private sealed record GuestMergeResult(
        IReadOnlyDictionary<Guid, Guid> TargetGuestIds,
        IReadOnlyDictionary<Guid, AttendanceStatus?> ResultingStatuses)
    {
        public static GuestMergeResult Empty { get; } = new(
            new Dictionary<Guid, Guid>(),
            new Dictionary<Guid, AttendanceStatus?>());
    }
}
