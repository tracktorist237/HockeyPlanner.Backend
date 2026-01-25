using HockeyPlanner.Backend.Application.Abstractions.Services;
using HockeyPlanner.Backend.Core.Entities;
using HockeyPlanner.Backend.Core.Enums;
using HockeyPlanner.Backend.Core.Exceptions;
using HockeyPlanner.Backend.Infrastructure.Data;
using HockeyPlanner.Backend.Shared.Models.Events;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace HockeyPlanner.Backend.Application.Implementations.Services
{
    public class EventService : IEventService
    {
        private readonly AppDbContext _context;
        private readonly ILogger<EventService> _logger;

        public EventService(AppDbContext context, ILogger<EventService> logger)
        {
            _context = context;
            _logger = logger;
        }

        public async Task<Guid> CreateEvent(CreateEventDto dto, Guid currentUserId)
        {
            _logger.LogInformation($"Создание мероприятия: {dto.Title}", dto.Title);

            // Проверка прав
            //var hasPermission = await CheckCreatePermission(currentUserId, dto.Type);
            //if (!hasPermission)
            //    throw new UnauthorizedException("Недостаточно прав для создания мероприятия");

            // Создание мероприятия
            var scheduledEvent = new ScheduledEvent
            {
                Title = dto.Title.Trim(),
                Description = dto.Description?.Trim(),
                Type = dto.Type,
                StartTime = dto.StartTime.ToUniversalTime(),
                EndTime = dto.EndTime.ToUniversalTime(),
                LocationName = dto.LocationName.Trim(),
                LocationAddress = dto.LocationAddress.Trim(),
                IceRinkNumber = dto.IceRinkNumber?.Trim(),
                Status = EventStatus.Scheduled,
                CreatedAt = DateTime.UtcNow
            };

            // Сохранение
            _context.Events.Add(scheduledEvent);
            await _context.SaveChangesAsync();

            _logger.LogInformation($"Мероприятие создано: {scheduledEvent.Id}");

            return scheduledEvent.Id;
        }

        public Task CancelEvent(Guid eventId, Guid currentUserId)
        {
            throw new NotImplementedException();
        }

        public async Task<EventListDto> GetAllEvents()
        {
            var result = new EventListDto();
            var events = await _context.Events.ToListAsync();

            foreach (var item in events)
            {
                result.Events.Add(new EventLookUpDto()
                {
                    Description = item.Description,
                    EndTime = item.EndTime,
                    IceRinkNumber= item.IceRinkNumber,
                    Id = item.Id,
                    LocationAddress = item.LocationAddress,
                    LocationName = item.LocationName,
                    StartTime = item.StartTime,
                    Status = item.Status,
                    Title = item.Title,
                    Type = item.Type
                });
            }

            return result;
        }

        public async Task<EventDto> GetEvent(Guid eventId)
        {
            var selectedEvent = await _context.Events.Include(e => e.Attendances).FirstOrDefaultAsync(e => e.Id == eventId);
            if (selectedEvent == null)
                throw new NotFoundException("Событие не найдено");

            var attendances = await _context.Attendances.Include(e => e.User).Where(e => e.EventId == eventId).ToListAsync();

            var attendanceDtos = new List<AttendanceLookUpDto>();
            foreach (var attend in attendances)
            {
                attendanceDtos.Add(new AttendanceLookUpDto()
                {
                    FirstName = attend.User.FirstName,
                    LastName = attend.User.LastName,
                    UserId = attend.User.Id,
                    Handedness = attend.User.Handedness,
                    JerseyNumber = attend.User.JerseyNumber,
                    Notes = attend.Notes,
                    PrimaryPosition = attend.User.PrimaryPosition,
                    RespondedAt = attend.RespondedAt,
                    Status = attend.Status,
                });
            }

            var lines = await _context.Lines.Include(e => e.Players).Where(e => e.EventId == eventId).ToListAsync();

            var rosterDto = new List<LineLookupDto>();
            foreach (var line in lines)
            {
                var playersDto = new List<PlayerLookUpDto>();
                var members = line.Players;
                foreach (var member in members) 
                {
                    playersDto.Add(new PlayerLookUpDto()
                    {
                        FirstName = member.FirstName,
                        LastName = member.LastName,
                        UserId = member.UserId,
                        JerseyNumber = member.JerseyNumber,
                        Role = member.Role,
                    });
                }

                rosterDto.Add(new LineLookupDto()
                {
                    Name = line.Name,
                    Order = line.Order,
                    Members = playersDto,
                });
            }

            var dto = new EventDto()
            {
                CreatedAt = selectedEvent.CreatedAt,
                Description = selectedEvent.Description,
                EndTime= selectedEvent.EndTime,
                IceRinkNumber = selectedEvent.IceRinkNumber,
                Id = selectedEvent.Id,
                LocationAddress= selectedEvent.LocationAddress,
                LocationName= selectedEvent.LocationName,
                StartTime= selectedEvent.StartTime,
                Status= selectedEvent.Status,
                Title= selectedEvent.Title,
                Type= selectedEvent.Type,
                UpdatedAt= selectedEvent.UpdatedAt,
                Attendances = attendanceDtos,
                Roster = rosterDto
            };

            return dto;
        }

        public async Task UpdateAttendance(Guid eventId, Guid userId, UpdateAttendanceRequest dto)
        {
            var selectedEvent = await _context.Events.Include(e => e.Attendances).FirstOrDefaultAsync(e => e.Id == eventId);
            if (selectedEvent == null)
                throw new NotFoundException("Событие не найдено");

            var user = await _context.Users.FirstOrDefaultAsync(p => p.Id == userId);
            if (user == null)
                throw new NotFoundException("Пользователь не найден");

            var attendance = selectedEvent.Attendances.FirstOrDefault(a => a.UserId == user.Id); 

            if (attendance is null)
            {
                attendance = new Attendance() 
                { 
                    UserId = userId, 
                    CreatedAt = DateTime.UtcNow,
                    Status = dto.Status,
                    Notes = dto.Notes,
                    UpdatedAt = DateTime.UtcNow,
                    RespondedAt = DateTime.UtcNow,
                    EventId = eventId,
                };
                _context.Attendances.Add(attendance);
            }
            else
            {
                attendance.Status = dto.Status;
                attendance.Notes = dto.Notes;
                attendance.UpdatedAt = DateTime.UtcNow;
                _context.Attendances.Update(attendance);
            }

            await _context.SaveChangesAsync();
        }

        private async Task<bool> CheckCreatePermission(Guid userId, EventType eventType)
        {
            var membership = await _context.Users.FirstOrDefaultAsync(u => u.Id == userId);

            if (membership == null)
                return false;

            // Тренер, менеджер и капитан могут создавать мероприятия
            return membership.Role == UserRole.Coach ||
                   membership.Role == UserRole.Manager ||
                   membership.Role == UserRole.Captain;
        }

        private EventDto MapToDto(ScheduledEvent scheduledEvent)
        {
            return new EventDto
            {
                Id = scheduledEvent.Id,
                Title = scheduledEvent.Title,
                Description = scheduledEvent.Description,
                Type = scheduledEvent.Type,
                StartTime = scheduledEvent.StartTime,
                EndTime = scheduledEvent.EndTime,
                Status = scheduledEvent.Status,
                
                LocationName = scheduledEvent.LocationName,
                LocationAddress = scheduledEvent.LocationAddress,
                IceRinkNumber = scheduledEvent.IceRinkNumber,
                
                CreatedAt = scheduledEvent.CreatedAt,
                UpdatedAt = scheduledEvent.UpdatedAt
            };
        }
    }
}
