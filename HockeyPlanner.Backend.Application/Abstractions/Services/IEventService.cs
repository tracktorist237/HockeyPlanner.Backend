using HockeyPlanner.Backend.Shared.Models.Events;

namespace HockeyPlanner.Backend.Application.Abstractions.Services
{
    public interface IEventService
    {
        Task<Guid> CreateEvent(CreateEventDto dto, Guid actorUserId, CancellationToken cancellationToken);
        Task<EventDto> GetEvent(Guid eventId, Guid? viewerUserId, CancellationToken cancellationToken);
        Task<EventListDto> GetAllEvents(Guid? viewerUserId, Guid? teamId, CancellationToken cancellationToken);
        Task<AttendanceLookUpDto> CreateEventGuest(
            Guid eventId,
            CreateEventGuestRequest dto,
            Guid actorUserId,
            CancellationToken cancellationToken);
        Task UpdateAttendance(
            Guid eventId,
            Guid targetUserId,
            UpdateAttendanceRequest dto,
            Guid actorUserId,
            CancellationToken cancellationToken);
        Task UpdateEventGuestAttendance(
            Guid eventId,
            Guid guestId,
            UpdateAttendanceRequest dto,
            Guid actorUserId,
            CancellationToken cancellationToken);
        Task<bool> DeleteEvent(Guid eventId, Guid actorUserId, CancellationToken cancellationToken);
        Task<Guid> UpdateEvent(
            UpdateEventDto dto,
            Guid eventId,
            Guid actorUserId,
            CancellationToken cancellationToken);
    }
}
