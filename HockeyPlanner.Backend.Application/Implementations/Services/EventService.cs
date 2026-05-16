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

            var hasPermission = await CanManageEventScope(dto.TeamId, currentUserId);
            if (!hasPermission)
                throw new UnauthorizedException("Недостаточно прав для создания мероприятия");

            // Создание мероприятия
            if (dto.Type == EventType.Game && dto.UniformColorId.HasValue)
            {
                var uniformColorExists = await _context.UniformColors
                    .AnyAsync(x => x.Id == dto.UniformColorId.Value);

                if (!uniformColorExists)
                    throw new BusinessRuleException("Выбранный цвет формы не найден в справочнике");
            }

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
                UniformColorId = dto.Type == EventType.Game ? dto.UniformColorId : null,
                TeamId = dto.TeamId,
            };

            if (dto.Type == EventType.Practice && dto.ExerciseIds.Count > 0)
            {
                var exerciseIds = dto.ExerciseIds.Distinct().ToList();
                var existingExerciseIds = await _context.Exercises
                    .Where(x => exerciseIds.Contains(x.Id))
                    .Select(x => x.Id)
                    .ToListAsync();

                if (existingExerciseIds.Count != exerciseIds.Count)
                    throw new BusinessRuleException("Некоторые упражнения из банка не найдены");

                scheduledEvent.ScheduledEventExercises = exerciseIds
                    .Select((exerciseId, index) => new ScheduledEventExercise
                    {
                        ScheduledEventId = scheduledEvent.Id,
                        ExerciseId = exerciseId,
                        Order = index + 1
                    })
                    .ToList();
            }

            var users = dto.TeamId.HasValue
                ? await _context.TeamMemberships
                    .Where(m => m.TeamId == dto.TeamId.Value)
                    .Select(m => m.User)
                    .ToListAsync()
                : await _context.Users.ToListAsync();
            var attendances = new List<Attendance>();

            foreach (var user in users)
            {
                attendances.Add(new Attendance()
                {
                    UserId = user.Id,
                    CreatedAt = DateTime.UtcNow,
                    RespondedAt = scheduledEvent.CreatedAt,
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

            var hasPermission = await CanManageEventScope(dto.TeamId, currentUserId);
            if (!hasPermission)
                throw new UnauthorizedException("Недостаточно прав для обновления мероприятия");

            // Создание мероприятия
            var scheduledEvent = await _context.Events.FirstOrDefaultAsync(e => e.Id == eventId);

            if (scheduledEvent == null)
                throw new NotFoundException("Мероприятие не найдено");

            if (dto.Type == EventType.Game && dto.UniformColorId.HasValue)
            {
                var uniformColorExists = await _context.UniformColors
                    .AnyAsync(x => x.Id == dto.UniformColorId.Value);

                if (!uniformColorExists)
                    throw new BusinessRuleException("Выбранный цвет формы не найден в справочнике");
            }

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
            scheduledEvent.UniformColorId = dto.Type == EventType.Game ? dto.UniformColorId : null;
            scheduledEvent.TeamId = dto.TeamId;

            var existingEventExercises = await _context.ScheduledEventExercises
                .Where(x => x.ScheduledEventId == eventId)
                .ToListAsync();
            _context.ScheduledEventExercises.RemoveRange(existingEventExercises);

            if (dto.Type == EventType.Practice && dto.ExerciseIds.Count > 0)
            {
                var exerciseIds = dto.ExerciseIds.Distinct().ToList();
                var existingExerciseIds = await _context.Exercises
                    .Where(x => exerciseIds.Contains(x.Id))
                    .Select(x => x.Id)
                    .ToListAsync();

                if (existingExerciseIds.Count != exerciseIds.Count)
                    throw new BusinessRuleException("Некоторые упражнения из банка не найдены");

                var newEventExercises = exerciseIds
                    .Select((exerciseId, index) => new ScheduledEventExercise
                    {
                        ScheduledEventId = eventId,
                        ExerciseId = exerciseId,
                        Order = index + 1
                    })
                    .ToList();

                await _context.ScheduledEventExercises.AddRangeAsync(newEventExercises);
            }

            // Сохранение
            _context.Events.Update(scheduledEvent);
            await _context.SaveChangesAsync();

            _logger.LogInformation($"Мероприятие обновлено: {scheduledEvent.Id}");

            return scheduledEvent.Id;
        }



        public async Task<EventListDto> GetAllEvents(Guid? currentUserId, Guid? teamId)
        {
            if (teamId.HasValue)
            {
                var teamProjection = await _context.Teams
                    .AsNoTracking()
                    .Where(t => t.Id == teamId.Value)
                    .Select(t => new { t.Visibility })
                    .FirstOrDefaultAsync();

                if (teamProjection == null)
                    throw new NotFoundException("Команда не найдена");

                if (teamProjection.Visibility == TeamVisibility.Private)
                {
                    if (!currentUserId.HasValue)
                        throw new UnauthorizedException("Недостаточно прав для просмотра мероприятий команды");

                    var isMember = await _context.TeamMemberships
                        .AsNoTracking()
                        .AnyAsync(m => m.TeamId == teamId.Value && m.UserId == currentUserId.Value);

                    if (!isMember)
                        throw new UnauthorizedException("Недостаточно прав для просмотра мероприятий команды");
                }
            }

            var query = _context.Events.AsNoTracking();
            if (teamId.HasValue)
            {
                query = query.Where(e => e.TeamId == teamId.Value);
            }
            else if (currentUserId.HasValue)
            {
                var userTeamIds = _context.TeamMemberships
                    .AsNoTracking()
                    .Where(m => m.UserId == currentUserId.Value)
                    .Select(m => m.TeamId);
                var currentUserIsGoalie = await _context.Users
                    .AsNoTracking()
                    .Where(u => u.Id == currentUserId.Value)
                    .Select(u => u.PrimaryPosition == Position.Goalie)
                    .FirstOrDefaultAsync();

                query = query.Where(e =>
                    (e.TeamId.HasValue && userTeamIds.Contains(e.TeamId.Value)) ||
                    (currentUserIsGoalie &&
                        e.GoalieRequest != null &&
                        e.GoalieRequest.Visibility == GoalieRequestVisibility.AllGoalies &&
                        e.GoalieRequest.Status == GoalieRequestStatus.Open));
            }

            var events = await query
                .OrderBy(e => e.StartTime)
                .Select(e => new EventLookUpDto()
                {
                    Id = e.Id,
                    Title = e.Title,
                    Description = e.Description,
                    StartTime = e.StartTime,
                    LocationName = e.LocationName,
                    LocationAddress = e.LocationAddress,
                    IceRinkNumber = e.IceRinkNumber,
                    Status = e.Status,
                    Type = e.Type,
                    LeagueName = e.LeagueName,
                    UniformColorId = e.UniformColorId,
                    TeamId = e.TeamId,
                    GoalieNeededCount = e.GoalieRequest == null ? null : e.GoalieRequest.NeededCount,
                    GoalieConfirmedCount = e.GoalieRequest == null
                        ? null
                        : e.GoalieRequest.Applications.Count(a => a.Status == GoalieApplicationStatus.Confirmed),
                    GoalieApplicationStatus = e.GoalieRequest == null
                        ? null
                        : e.GoalieRequest.Applications
                            .Where(a => a.GoalieUserId == currentUserId)
                            .Select(a => (GoalieApplicationStatus?)a.Status)
                            .FirstOrDefault(),
                    AttendanceStatus = e.Attendances
                        .Where(a => a.UserId == currentUserId)
                        .Select(a => a.Status)
                        .FirstOrDefault()
                })
                .ToListAsync();

            return new EventListDto { Events = events };
        }

        public async Task<EventDto> GetEvent(Guid eventId)
        {
            var selectedEvent = await _context.Events
                .AsNoTracking()
                .Include(e => e.Roster)
                    .ThenInclude(r => r.Players)
                .Include(e => e.Attendances)
                    .ThenInclude(a => a.User)
                .Include(e => e.UniformColor)
                .Include(e => e.ScheduledEventExercises)
                    .ThenInclude(x => x.Exercise)
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
                HomeTeamName = selectedEvent.HomeTeamName,
                UniformColorId = selectedEvent.UniformColorId,
                TeamId = selectedEvent.TeamId,
                UniformColor = selectedEvent.UniformColor == null
                    ? null
                    : new Shared.Models.UniformColors.UniformColorDto
                    {
                        Id = selectedEvent.UniformColor.Id,
                        Name = selectedEvent.UniformColor.Name,
                        ImageUrl = selectedEvent.UniformColor.ImageUrl
                    },
                Exercises = selectedEvent.ScheduledEventExercises
                    .OrderBy(x => x.Order)
                    .Select(x => new Shared.Models.Exercises.ExerciseDto
                    {
                        Id = x.Exercise.Id,
                        Name = x.Exercise.Name,
                        VideoUrl = x.Exercise.VideoUrl
                    })
                    .ToList()
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
            var now = DateTime.UtcNow;

            if (attendance is null)
            {
                attendance = new Attendance() 
                { 
                    UserId = userId, 
                    CreatedAt = now,
                    Status = dto.Status,
                    Notes = dto.Notes,
                    UpdatedAt = now,
                    RespondedAt = now,
                    EventId = eventId,
                };
                await _context.Attendances.AddAsync(attendance);
            }
            else
            {
                await _context.Attendances
                    .Where(a => a.EventId == eventId && a.UserId == userId)
                    .ExecuteUpdateAsync(setters => setters
                        .SetProperty(a => a.Status, dto.Status)
                        .SetProperty(a => a.Notes, dto.Notes)
                        .SetProperty(a => a.RespondedAt, now)
                        .SetProperty(a => a.UpdatedAt, now));
            }

            var player = await _context.Players
                .Include(p => p.Line)
                .FirstOrDefaultAsync(player => player.UserId == userId && player.Line.EventId == eventId);

            if((dto.Status == AttendanceStatus.Declined || dto.Status == AttendanceStatus.Pending) && player != null)
            {
                _context.Players.Remove(player);
            }

            await _context.SaveChangesAsync();

            _logger.LogInformation("Attendance updated. EventId={EventId}, UserId={UserId}, Status={Status}, RespondedAt={RespondedAt}",
                eventId, userId, dto.Status, now);
        }

        public async Task<bool> DeleteEvent(Guid eventId, Guid currentUserId)
        {
            var user = await _context.Users.FirstOrDefaultAsync(u => u.Id == currentUserId);

            if(user == null)
                return false;

            var eventTeamId = await _context.Events
                .AsNoTracking()
                .Where(e => e.Id == eventId)
                .Select(e => e.TeamId)
                .FirstOrDefaultAsync();

            var canManage = await CanManageEventScope(eventTeamId, currentUserId);
            if (!canManage)
                return false;

            var deletedRows = await _context.Events.Where(e => e.Id == eventId).ExecuteDeleteAsync();

            return deletedRows > 0;
        }

        private async Task<bool> CanManageEventScope(Guid? teamId, Guid currentUserId)
        {
            if (!teamId.HasValue)
                return false;

            var teamExists = await _context.Teams
                .AsNoTracking()
                .AnyAsync(t => t.Id == teamId.Value);

            if (!teamExists)
                throw new NotFoundException("Команда не найдена");

            return await _context.TeamMemberships
                .AsNoTracking()
                .AnyAsync(m =>
                    m.TeamId == teamId.Value &&
                    m.UserId == currentUserId &&
                    (m.Role == TeamMemberRole.Owner || m.Role == TeamMemberRole.Admin));
        }
    }
}
