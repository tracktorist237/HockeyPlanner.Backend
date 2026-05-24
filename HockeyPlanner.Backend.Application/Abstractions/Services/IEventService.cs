using HockeyPlanner.Backend.Shared.Models.Events;

namespace HockeyPlanner.Backend.Application.Abstractions.Services
{
    public interface IEventService
    {
        Task<Guid> CreateEvent(CreateEventDto dto, Guid currentUserId);
        Task<EventDto> GetEvent(Guid eventId);
        Task<EventListDto> GetAllEvents(Guid? currentUserId, Guid? teamId);
        Task<AttendanceLookUpDto> CreateEventGuest(Guid eventId, CreateEventGuestRequest dto, Guid currentUserId);
        Task UpdateAttendance(Guid eventId, Guid userId, UpdateAttendanceRequest dto, Guid? currentUserId = null);
        Task UpdateEventGuestAttendance(Guid eventId, Guid guestId, UpdateAttendanceRequest dto, Guid currentUserId);
        Task<bool> DeleteEvent(Guid eventId, Guid currentUserId);
        Task<Guid> UpdateEvent(UpdateEventDto dto, Guid eventId, Guid currentUserId);
    }
}
