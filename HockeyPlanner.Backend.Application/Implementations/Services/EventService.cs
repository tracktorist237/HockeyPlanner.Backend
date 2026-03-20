using HockeyPlanner.Backend.Application.Abstractions.Services;
using HockeyPlanner.Backend.Core.Entities;
using HockeyPlanner.Backend.Core.Enums;
using HockeyPlanner.Backend.Core.Exceptions;
using HockeyPlanner.Backend.Infrastructure.Data;
using HockeyPlanner.Backend.Shared;
using HockeyPlanner.Backend.Shared.Models.Events;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace HockeyPlanner.Backend.Application.Implementations.Services
{
    internal class EventService : IEventService
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
            var currentUser = await _context.Users.FirstOrDefaultAsync(u => u.Id == currentUserId);

            if (currentUser == null)
                throw new NotFoundException("Пользователь не найден");

            //Проверка прав
            var hasPermission = PermissionHelper.CheckCreatePermission(currentUser.Role);
            if (!hasPermission)
                throw new UnauthorizedException("Недостаточно прав для создания мероприятия");

            // Создание мероприятия
            var scheduledEvent = new ScheduledEvent
            {
                Title = dto.Title.Trim(),
                Description = dto.Description?.Trim(),
                Type = dto.Type,
                StartTime = dto.StartTime.ToUniversalTime(),
                LocationName = dto.LocationName.Trim(),
                LocationAddress = dto.LocationAddress.Trim(),
                IceRinkNumber = dto.IceRinkNumber?.Trim(),
                Status = EventStatus.Scheduled,
                CreatedAt = DateTime.UtcNow,
                AwayTeamName = dto.AwayTeamName?.Trim(),
                HomeTeamName = dto.HomeTeamName?.Trim(),
                LeagueName = dto.LeagueName?.Trim(),
            };

            var users = await _context.Users.ToListAsync();
            var attendances = new List<Attendance>();

            foreach (var user in users)
            {
                attendances.Add(new Attendance()
                {
                    UserId = user.Id,
                    CreatedAt = DateTime.UtcNow,
                    Status = AttendanceStatus.Pending,
                    EventId = scheduledEvent.Id,
                });
            }

            scheduledEvent.Attendances = attendances;
            // Сохранение
            await _context.Events.AddAsync(scheduledEvent);
            await _context.SaveChangesAsync();

            _logger.LogInformation($"Мероприятие создано: {scheduledEvent.Id}");

            return scheduledEvent.Id;
        }

        public async Task<Guid> UpdateEvent(UpdateEventDto dto, Guid eventId, Guid currentUserId)
        {
            _logger.LogInformation($"Обновление мероприятия: {dto.Title}", dto.Title);
            var currentUser = await _context.Users.FirstOrDefaultAsync(u => u.Id == currentUserId);

            if (currentUser == null)
                throw new NotFoundException("Пользователь не найден");

            //Проверка прав
            var hasPermission = PermissionHelper.CheckCreatePermission(currentUser.Role);
            if (!hasPermission)
                throw new UnauthorizedException("Недостаточно прав для обновления мероприятия");

            // Создание мероприятия
            var scheduledEvent = await _context.Events.FirstOrDefaultAsync(e => e.Id == eventId);

            if (scheduledEvent == null)
                throw new NotFoundException("Мероприятие не найдено");

            scheduledEvent.Title = dto.Title.Trim();
            scheduledEvent.Description = dto.Description?.Trim();
            scheduledEvent.Type = dto.Type;
            scheduledEvent.StartTime = dto.StartTime.ToUniversalTime();
            scheduledEvent.LocationName = dto.LocationName.Trim();
            scheduledEvent.LocationAddress = dto.LocationAddress.Trim();
            scheduledEvent.IceRinkNumber = dto.IceRinkNumber?.Trim();
            scheduledEvent.Status = dto.Status;
            scheduledEvent.UpdatedAt = DateTime.UtcNow;
            scheduledEvent.AwayTeamName = dto.AwayTeamName;
            scheduledEvent.HomeTeamName = dto.HomeTeamName;
            scheduledEvent.LeagueName = dto.LeagueName;

            // Сохранение
            _context.Events.Update(scheduledEvent);
            await _context.SaveChangesAsync();

            _logger.LogInformation($"Мероприятие обновлено: {scheduledEvent.Id}");

            return scheduledEvent.Id;
        }



        public async Task<EventListDto> GetAllEvents()
        {
            var result = new EventListDto();
            var events = await _context.Events.OrderBy(e => e.StartTime).ToListAsync();

            foreach (var item in events)
            {
                result.Events?.Add(new EventLookUpDto()
                {
                    Description = item.Description,
                    IceRinkNumber= item.IceRinkNumber,
                    Id = item.Id,
                    LocationAddress = item.LocationAddress,
                    LocationName = item.LocationName,
                    StartTime = item.StartTime,
                    Status = item.Status,
                    Title = item.Title,
                    Type = item.Type,
                    LeagueName = item.LeagueName,
                });
            }

            return result;
        }

        public async Task<EventDto> GetEvent(Guid eventId)
        {
            var selectedEvent = await _context.Events
                .AsNoTracking()
                .Include(e => e.Roster)
                    .ThenInclude(r => r.Players)
                .Include(e => e.Attendances)
                    .ThenInclude(a => a.User)
                .FirstOrDefaultAsync(e => e.Id == eventId);

            if (selectedEvent == null)
                throw new NotFoundException("Событие не найдено");

            var attendances = selectedEvent.Attendances.Where(e => e.EventId == eventId);

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

            // Сортировка attendanceDtos:
            // 1. По статусу: Confirmed (2) → Declined (3) → Pending (1)
            // 2. Внутри каждой группы по RespondedAt (кто позже ответил — выше)
            attendanceDtos = attendanceDtos
                .OrderByDescending(a => a.Status == AttendanceStatus.Confirmed)   // Confirmed первыми
                .ThenByDescending(a => a.Status == AttendanceStatus.Declined)     // Declined вторыми
                .ThenBy(a => a.Status == AttendanceStatus.Pending)                // Pending последними
                .ThenByDescending(a => a.RespondedAt)                              // Внутри группы по времени ответа (новые выше)
                .ToList();

            var lines = selectedEvent.Roster.Where(e => e.EventId == eventId);

            var rosterDto = new List<LineDto>();
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
                        PlayerId = member.Id,
                        Role = member.Role,
                    });
                }

                rosterDto.Add(new LineDto()
                {
                    Id = line.Id,
                    Name = line.Name,
                    Order = line.Order,
                    Members = playersDto,
                });
            }

            var dto = new EventDto()
            {
                CreatedAt = selectedEvent.CreatedAt,
                Description = selectedEvent.Description,
                IceRinkNumber = selectedEvent.IceRinkNumber,
                Id = selectedEvent.Id,
                LocationAddress = selectedEvent.LocationAddress,
                LocationName = selectedEvent.LocationName,
                StartTime = selectedEvent.StartTime,
                Status = selectedEvent.Status,
                Title = selectedEvent.Title,
                Type = selectedEvent.Type,
                UpdatedAt = selectedEvent.UpdatedAt,
                Attendances = attendanceDtos,
                Roster = rosterDto,
                AwayTeamName = selectedEvent.AwayTeamName,
                LeagueName = selectedEvent.LeagueName,
                HomeTeamName = selectedEvent.HomeTeamName
            };

            return dto;
        }

        public async Task UpdateAttendance(Guid eventId, Guid userId, UpdateAttendanceRequest dto)
        {
            var user = await _context.Users.FirstOrDefaultAsync(p => p.Id == userId);
            if (user == null)
                throw new NotFoundException("Пользователь не найден");

            var selectedEvent = await _context.Events.Include(e => e.Attendances).FirstOrDefaultAsync(e => e.Id == eventId);
            if (selectedEvent == null)
                throw new NotFoundException("Событие не найдено");

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
                await _context.Attendances.AddAsync(attendance);
            }
            else
            {
                attendance.Status = dto.Status;
                attendance.Notes = dto.Notes == null ? attendance.Notes : dto.Notes;
                attendance.UpdatedAt = DateTime.UtcNow;
                _context.Attendances.Update(attendance);
            }

            var player = await _context.Players
                .Include(p => p.Line)
                .FirstOrDefaultAsync(player => player.UserId == userId && player.Line.EventId == eventId);

            if((dto.Status == AttendanceStatus.Declined || dto.Status == AttendanceStatus.Pending) && player != null)
            {
                _context.Players.Remove(player);
            }

            await _context.SaveChangesAsync();
        }

        public async Task<bool> DeleteEvent(Guid eventId, Guid currentUserId)
        {
            var user = await _context.Users.FirstOrDefaultAsync(u => u.Id == currentUserId);

            if(user == null || !PermissionHelper.CheckCreatePermission(user.Role))
                return false;

            var deletedRows = await _context.Events.Where(e => e.Id == eventId).ExecuteDeleteAsync();

            return deletedRows > 0;
        }
    }
}
