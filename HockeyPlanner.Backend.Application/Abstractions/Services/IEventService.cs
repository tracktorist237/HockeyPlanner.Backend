using HockeyPlanner.Backend.Shared.Models.Events;

namespace HockeyPlanner.Backend.Application.Abstractions.Services
{
    public interface IEventService
    {
        Task<Guid> CreateEvent(CreateEventDto dto, Guid currentUserId);
        Task<EventDto> GetEvent(Guid eventId);
        Task<EventListDto> GetAllEvents();
        Task UpdateAttendance(Guid eventId, Guid userId, UpdateAttendanceRequest dto);
        Task CancelEvent(Guid eventId, Guid currentUserId);
    }
}
