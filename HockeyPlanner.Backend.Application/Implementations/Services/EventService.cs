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
                    .AnyAsync(x => x.Id == dto.UniformColorId.Value && x.TeamId == dto.TeamId);

                if (!uniformColorExists)
                    throw new BusinessRuleException("Выбранный цвет формы не найден для этой команды");
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
                    .Where(x => exerciseIds.Contains(x.Id) && x.TeamId == dto.TeamId)
                    .Select(x => x.Id)
                    .ToListAsync();

                if (existingExerciseIds.Count != exerciseIds.Count)
                    throw new BusinessRuleException("Некоторые упражнения из банка не найдены для этой команды");

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
                    .AnyAsync(x => x.Id == dto.UniformColorId.Value && x.TeamId == dto.TeamId);

                if (!uniformColorExists)
                    throw new BusinessRuleException("Выбранный цвет формы не найден для этой команды");
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
                    .Where(x => exerciseIds.Contains(x.Id) && x.TeamId == dto.TeamId)
                    .Select(x => x.Id)
                    .ToListAsync();

                if (existingExerciseIds.Count != exerciseIds.Count)
                    throw new BusinessRuleException("Некоторые упражнения из банка не найдены для этой команды");

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
                        .ThenInclude(p => p.EventGuest)
                .Include(e => e.Roster)
                    .ThenInclude(r => r.UniformColor)
                .Include(e => e.Attendances)
                    .ThenInclude(a => a.User)
                .Include(e => e.EventGuests)
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

            foreach (var guest in selectedEvent.EventGuests.Where(g => g.EventId == eventId))
            {
                attendanceDtos.Add(new AttendanceLookUpDto()
                {
                    FirstName = guest.FirstName,
                    LastName = guest.LastName,
                    UserId = guest.Id,
                    Handedness = guest.Handedness,
                    JerseyNumber = guest.JerseyNumber,
                    Notes = guest.Notes,
                    PrimaryPosition = null,
                    RespondedAt = guest.RespondedAt,
                    Status = guest.Status,
                    IsGuest = true,
                    InvitedByUserId = guest.InvitedByUserId,
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
                        UserId = member.EventGuestId ?? member.UserId!.Value,
                        JerseyNumber = member.JerseyNumber,
                        PlayerId = member.Id,
                        Role = member.Role,
                        IsGuest = member.EventGuestId.HasValue,
                        InvitedByUserId = member.EventGuest?.InvitedByUserId,
                    });
                }

                rosterDto.Add(new LineDto()
                {
                    Id = line.Id,
                    Name = line.Name,
                    Order = line.Order,
                    UniformColorId = line.UniformColorId,
                    UniformColor = line.UniformColor == null
                        ? null
                        : new Shared.Models.UniformColors.UniformColorDto
                        {
                            Id = line.UniformColor.Id,
                            Name = line.UniformColor.Name,
                            ImageUrl = line.UniformColor.ImageUrl,
                            TeamId = line.UniformColor.TeamId
                        },
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
                        ImageUrl = selectedEvent.UniformColor.ImageUrl,
                        TeamId = selectedEvent.UniformColor.TeamId
                    },
                Exercises = selectedEvent.ScheduledEventExercises
                    .OrderBy(x => x.Order)
                    .Select(x => new Shared.Models.Exercises.ExerciseDto
                    {
                        Id = x.Exercise.Id,
                        Name = x.Exercise.Name,
                        VideoUrl = x.Exercise.VideoUrl,
                        TeamId = x.Exercise.TeamId
                    })
                    .ToList()
            };

            return dto;
        }

        public async Task<AttendanceLookUpDto> CreateEventGuest(Guid eventId, CreateEventGuestRequest dto, Guid currentUserId)
        {
            var currentUser = await _context.Users.FirstOrDefaultAsync(u => u.Id == currentUserId);
            if (currentUser == null)
                throw new NotFoundException("Пользователь не найден");

            var selectedEvent = await _context.Events.FirstOrDefaultAsync(e => e.Id == eventId);
            if (selectedEvent == null)
                throw new NotFoundException("Событие не найдено");

            var canAccess = await CanAccessEventScope(selectedEvent.TeamId, currentUserId);
            if (!canAccess)
                throw new UnauthorizedException("Недостаточно прав для добавления гостя");

            var firstName = dto.FirstName?.Trim() ?? string.Empty;
            var lastName = dto.LastName?.Trim() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(firstName) || string.IsNullOrWhiteSpace(lastName))
                throw new BusinessRuleException("Укажите имя и фамилию гостя");

            var now = DateTime.UtcNow;
            var guest = new EventGuest
            {
                EventId = eventId,
                InvitedByUserId = currentUserId,
                FirstName = firstName,
                LastName = lastName,
                Handedness = dto.Handedness,
                JerseyNumber = dto.JerseyNumber,
                Status = AttendanceStatus.Confirmed,
                RespondedAt = now,
                CreatedAt = now,
                UpdatedAt = now,
            };

            await _context.EventGuests.AddAsync(guest);
            await _context.SaveChangesAsync();

            return new AttendanceLookUpDto
            {
                UserId = guest.Id,
                FirstName = guest.FirstName,
                LastName = guest.LastName,
                Handedness = guest.Handedness,
                JerseyNumber = guest.JerseyNumber,
                PrimaryPosition = null,
                Status = guest.Status,
                RespondedAt = guest.RespondedAt,
                Notes = guest.Notes,
                IsGuest = true,
                InvitedByUserId = guest.InvitedByUserId,
            };
        }

        public async Task UpdateAttendance(Guid eventId, Guid userId, UpdateAttendanceRequest dto, Guid? currentUserId = null)
        {
            var user = await _context.Users.FirstOrDefaultAsync(p => p.Id == userId);
            if (user == null)
                throw new NotFoundException("Пользователь не найден");

            var selectedEvent = await _context.Events.Include(e => e.Attendances).FirstOrDefaultAsync(e => e.Id == eventId);
            if (selectedEvent == null)
                throw new NotFoundException("Событие не найдено");

            if (currentUserId.HasValue && currentUserId.Value != userId)
            {
                var canManage = await CanManageEventScope(selectedEvent.TeamId, currentUserId.Value);
                if (!canManage)
                    throw new UnauthorizedException("Недостаточно прав для изменения чужой явки");
            }

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

        public async Task UpdateEventGuestAttendance(Guid eventId, Guid guestId, UpdateAttendanceRequest dto, Guid currentUserId)
        {
            var currentUser = await _context.Users.FirstOrDefaultAsync(u => u.Id == currentUserId);
            if (currentUser == null)
                throw new NotFoundException("Пользователь не найден");

            var selectedEvent = await _context.Events.FirstOrDefaultAsync(e => e.Id == eventId);
            if (selectedEvent == null)
                throw new NotFoundException("Событие не найдено");

            var guest = await _context.EventGuests.FirstOrDefaultAsync(g => g.Id == guestId && g.EventId == eventId);
            if (guest == null)
                throw new NotFoundException("Гость не найден");

            var canManage = await CanManageEventScope(selectedEvent.TeamId, currentUserId);
            if (!canManage && guest.InvitedByUserId != currentUserId)
                throw new UnauthorizedException("Недостаточно прав для изменения явки гостя");

            var now = DateTime.UtcNow;
            guest.Status = dto.Status;
            guest.Notes = dto.Notes;
            guest.RespondedAt = now;
            guest.UpdatedAt = now;

            var player = await _context.Players
                .Include(p => p.Line)
                .FirstOrDefaultAsync(player => player.EventGuestId == guestId && player.Line.EventId == eventId);

            if ((dto.Status == AttendanceStatus.Declined || dto.Status == AttendanceStatus.Pending) && player != null)
            {
                _context.Players.Remove(player);
            }

            await _context.SaveChangesAsync();

            _logger.LogInformation("Event guest attendance updated. EventId={EventId}, GuestId={GuestId}, Status={Status}, RespondedAt={RespondedAt}",
                eventId, guestId, dto.Status, now);
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

        private async Task<bool> CanAccessEventScope(Guid? teamId, Guid currentUserId)
        {
            if (!teamId.HasValue)
                return true;

            var teamExists = await _context.Teams
                .AsNoTracking()
                .AnyAsync(t => t.Id == teamId.Value);

            if (!teamExists)
                throw new NotFoundException("Команда не найдена");

            return await _context.TeamMemberships
                .AsNoTracking()
                .AnyAsync(m => m.TeamId == teamId.Value && m.UserId == currentUserId);
        }
    }
}
