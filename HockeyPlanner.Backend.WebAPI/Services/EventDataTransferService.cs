using HockeyPlanner.Backend.Core.Entities;
using HockeyPlanner.Backend.Core.Enums;
using HockeyPlanner.Backend.Core.Exceptions;
using HockeyPlanner.Backend.Infrastructure.Data;
using HockeyPlanner.Backend.WebAPI.Models.Events;
using Microsoft.EntityFrameworkCore;

namespace HockeyPlanner.Backend.WebAPI.Services;

public interface IEventDataTransferService
{
    Task TransferAsync(Guid sourceEventId, Guid actorUserId, TransferEventDataRequest request, CancellationToken cancellationToken);
}

public sealed class EventDataTransferService(AppDbContext context) : IEventDataTransferService
{
    public async Task TransferAsync(Guid sourceEventId, Guid actorUserId, TransferEventDataRequest request, CancellationToken cancellationToken)
    {
        if (sourceEventId == request.TargetEventId)
            throw new BusinessRuleException("Исходное и целевое мероприятия должны различаться.");

        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);
        var events = await context.Events
            .Include(value => value.Attendances)
            .Include(value => value.EventGuests)
            .Where(value => value.Id == sourceEventId || value.Id == request.TargetEventId)
            .ToListAsync(cancellationToken);
        var source = events.SingleOrDefault(value => value.Id == sourceEventId)
            ?? throw new NotFoundException("Исходное мероприятие не найдено");
        var target = events.SingleOrDefault(value => value.Id == request.TargetEventId)
            ?? throw new NotFoundException("Целевое мероприятие не найдено");
        if (!source.TeamId.HasValue || source.TeamId != target.TeamId)
            throw new BusinessRuleException("Мероприятия должны принадлежать одной команде.");
        var canManage = await context.TeamMemberships.AsNoTracking().AnyAsync(value =>
            value.TeamId == source.TeamId && value.UserId == actorUserId &&
            (value.Role == TeamMemberRole.Owner || value.Role == TeamMemberRole.Admin), cancellationToken);
        if (!canManage) throw new UnauthorizedException("Недостаточно прав для переноса данных мероприятий.");

        var sourceRoster = request.Roster
            ? await context.Lines.AsNoTracking().Include(line => line.Players)
                .Where(line => line.EventId == source.Id).OrderBy(line => line.Order).ToListAsync(cancellationToken)
            : [];
        var targetLineIds = request.Roster
            ? await context.Lines.AsNoTracking().Where(line => line.EventId == target.Id)
                .Select(line => line.Id).ToListAsync(cancellationToken)
            : [];

        if (request.Attendance) MergeAttendance(context, source, target);
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
    }

    private static void MergeAttendance(AppDbContext context, ScheduledEvent source, ScheduledEvent target)
    {
        var targetByUser = target.Attendances.ToDictionary(value => value.UserId);
        foreach (var item in source.Attendances)
        {
            if (!targetByUser.TryGetValue(item.UserId, out var current))
            {
                var copy = new Attendance { EventId = target.Id, UserId = item.UserId, Status = item.Status, Notes = item.Notes, RespondedAt = item.RespondedAt };
                context.Attendances.Add(copy);
            }
            else if (current.Status == AttendanceStatus.Pending && item.Status != AttendanceStatus.Pending)
            {
                current.Status = item.Status;
                current.Notes = item.Notes;
                current.RespondedAt = item.RespondedAt;
            }
        }
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
