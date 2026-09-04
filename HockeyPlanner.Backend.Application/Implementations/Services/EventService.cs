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
        private readonly INotificationService _notificationService;

        public EventService(AppDbContext context, ILogger<EventService> logger, INotificationService notificationService)
        {
            _context = context;
            _logger = logger;
            _notificationService = notificationService;
        }

        public async Task<Guid> CreateEvent(
            CreateEventDto dto,
            Guid actorUserId,
            CancellationToken cancellationToken)
        {
            _logger.LogInformation($"Создание мероприятия: {dto.Title}", dto.Title);
            var currentUser = await _context.Users.FirstOrDefaultAsync(
                u => u.Id == actorUserId,
                cancellationToken);

            if (currentUser == null)
                throw new NotFoundException("Пользователь не найден");

            var hasPermission = await CanManageEventScope(dto.TeamId, actorUserId, cancellationToken);
            if (!hasPermission)
                throw new UnauthorizedException("Недостаточно прав для создания мероприятия");

            // Создание мероприятия
            if (dto.Type == EventType.Game && dto.UniformColorId.HasValue)
            {
                var uniformColorExists = await _context.UniformColors
                    .AnyAsync(
                        x => x.Id == dto.UniformColorId.Value && x.TeamId == dto.TeamId,
                        cancellationToken);

                if (!uniformColorExists)
                    throw new BusinessRuleException("Выбранный цвет формы не найден для этой команды");
            }

            var scheduledEvent = new ScheduledEvent
            {
                Title = dto.Title.Trim(),
                Description = dto.Description?.Trim(),
                Type = dto.Type,
                StartTime = dto.StartTime.ToUniversalTime(),
                DurationMinutes = dto.DurationMinutes,
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
                    .ToListAsync(cancellationToken);

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
                    .ToListAsync(cancellationToken)
                : await _context.Users.ToListAsync(cancellationToken);
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
            await _context.Events.AddAsync(scheduledEvent, cancellationToken);
            await _context.SaveChangesAsync(cancellationToken);

            if (scheduledEvent.TeamId.HasValue)
            {
                await _notificationService.NotifyTeamAsync(
                    scheduledEvent.TeamId.Value,
                    NotificationType.EventPublished,
                    NotificationCategory.AttendanceRequired,
                    "Новое мероприятие",
                    $"{scheduledEvent.Title}: отметьтесь, сможете ли быть.",
                    $"/events/{scheduledEvent.Id}",
                    cancellationToken);
            }

            _logger.LogInformation($"Мероприятие создано: {scheduledEvent.Id}");

            return scheduledEvent.Id;
        }

        public async Task<Guid> UpdateEvent(
            UpdateEventDto dto,
            Guid eventId,
            Guid actorUserId,
            CancellationToken cancellationToken)
        {
            _logger.LogInformation($"Обновление мероприятия: {dto.Title}", dto.Title);

            var scheduledEvent = await _context.Events.FirstOrDefaultAsync(
                e => e.Id == eventId,
                cancellationToken);

            if (scheduledEvent == null)
                throw new NotFoundException("Мероприятие не найдено");

            var currentUser = await _context.Users.FirstOrDefaultAsync(
                u => u.Id == actorUserId,
                cancellationToken);

            if (currentUser == null)
                throw new NotFoundException("Пользователь не найден");

            var hasPermission = await CanManageEventScope(
                scheduledEvent.TeamId,
                actorUserId,
                cancellationToken);
            if (!hasPermission)
                throw new UnauthorizedException("Недостаточно прав для обновления мероприятия");

            if (dto.TeamId != scheduledEvent.TeamId)
                throw new BusinessRuleException("Перенос мероприятия между командами не поддерживается");

            if (dto.Type == EventType.Game && dto.UniformColorId.HasValue)
            {
                var uniformColorExists = await _context.UniformColors
                    .AnyAsync(
                        x => x.Id == dto.UniformColorId.Value && x.TeamId == scheduledEvent.TeamId,
                        cancellationToken);

                if (!uniformColorExists)
                    throw new BusinessRuleException("Выбранный цвет формы не найден для этой команды");
            }

            scheduledEvent.Title = dto.Title.Trim();
            scheduledEvent.Description = dto.Description?.Trim();
            scheduledEvent.Type = dto.Type;
            scheduledEvent.StartTime = dto.StartTime.ToUniversalTime();
            scheduledEvent.DurationMinutes = dto.DurationMinutes;
            scheduledEvent.LocationName = dto.LocationName.Trim();
            scheduledEvent.LocationAddress = dto.LocationAddress.Trim();
            scheduledEvent.IceRinkNumber = dto.IceRinkNumber?.Trim();
            scheduledEvent.Status = dto.Status;
            scheduledEvent.UpdatedAt = DateTime.UtcNow;
            scheduledEvent.AwayTeamName = dto.AwayTeamName;
            scheduledEvent.HomeTeamName = dto.HomeTeamName;
            scheduledEvent.LeagueName = dto.LeagueName;
            scheduledEvent.UniformColorId = dto.Type == EventType.Game ? dto.UniformColorId : null;
            var existingEventExercises = await _context.ScheduledEventExercises
                .Where(x => x.ScheduledEventId == eventId)
                .ToListAsync(cancellationToken);
            _context.ScheduledEventExercises.RemoveRange(existingEventExercises);

            if (dto.Type == EventType.Practice && dto.ExerciseIds.Count > 0)
            {
                var exerciseIds = dto.ExerciseIds.Distinct().ToList();
                var existingExerciseIds = await _context.Exercises
                    .Where(x => exerciseIds.Contains(x.Id) && x.TeamId == scheduledEvent.TeamId)
                    .Select(x => x.Id)
                    .ToListAsync(cancellationToken);

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

                await _context.ScheduledEventExercises.AddRangeAsync(newEventExercises, cancellationToken);
            }

            // Сохранение
            _context.Events.Update(scheduledEvent);
            await _context.SaveChangesAsync(cancellationToken);

            _logger.LogInformation($"Мероприятие обновлено: {scheduledEvent.Id}");

            return scheduledEvent.Id;
        }



        public async Task<EventListDto> GetAllEvents(
            Guid? viewerUserId,
            Guid? teamId,
            CancellationToken cancellationToken)
        {
            if (teamId.HasValue)
            {
                var teamProjection = await _context.Teams
                    .AsNoTracking()
                    .Where(t => t.Id == teamId.Value)
                    .Select(t => new { t.Visibility })
                    .FirstOrDefaultAsync(cancellationToken);

                if (teamProjection == null)
                    throw new NotFoundException("Команда не найдена");

                if (teamProjection.Visibility == TeamVisibility.Private)
                {
                    if (!viewerUserId.HasValue)
                        throw new UnauthorizedException("Недостаточно прав для просмотра мероприятий команды");

                    var isMember = await _context.TeamMemberships
                        .AsNoTracking()
                        .AnyAsync(
                            m => m.TeamId == teamId.Value && m.UserId == viewerUserId.Value,
                            cancellationToken);

                    if (!isMember)
                        throw new UnauthorizedException("Недостаточно прав для просмотра мероприятий команды");
                }
            }

            var query = _context.Events.AsNoTracking();
            if (teamId.HasValue)
            {
                query = query.Where(e => e.TeamId == teamId.Value && e.Team != null);
            }
            else if (viewerUserId.HasValue)
            {
                var userTeamIds = _context.TeamMemberships
                    .AsNoTracking()
                    .Where(m => m.UserId == viewerUserId.Value)
                    .Select(m => m.TeamId);
                var viewerIsGoalie = await _context.Users
                    .AsNoTracking()
                    .Where(user => user.Id == viewerUserId.Value)
                    .Select(user => user.PrimaryPosition == Position.Goalie)
                    .FirstOrDefaultAsync(cancellationToken);

                query = query.Where(e =>
                    e.TeamId.HasValue &&
                    e.Team != null &&
                    (userTeamIds.Contains(e.TeamId.Value) ||
                        (viewerIsGoalie &&
                            e.GoalieRequest != null &&
                            e.GoalieRequest.Visibility == GoalieRequestVisibility.AllGoalies &&
                            e.GoalieRequest.Status == GoalieRequestStatus.Open)));
            }
            else
            {
                query = query.Where(e =>
                    e.TeamId.HasValue &&
                    e.Team != null &&
                    e.Team.Visibility == TeamVisibility.Public);
            }

            var events = await query
                .OrderBy(e => e.StartTime)
                .Select(e => new EventLookUpDto()
                {
                    Id = e.Id,
                    Title = e.Title,
                    Description = e.Description,
                    StartTime = e.StartTime,
                    DurationMinutes = e.DurationMinutes,
                    LocationName = e.LocationName,
                    LocationAddress = e.LocationAddress,
                    IceRinkNumber = e.IceRinkNumber,
                    Status = e.Status,
                    Type = e.Type,
                    LeagueName = e.LeagueName,
                    UniformColorId = e.UniformColorId,
                    TeamId = e.TeamId,
                    TeamName = e.Team == null ? null : e.Team.Name,
                    HomeTeamName = e.HomeTeamName,
                    AwayTeamName = e.AwayTeamName,
                    ExternalLeagueProvider = e.ExternalLeagueProvider,
                    ExternalDivisionName = e.ExternalDivisionName,
                    ExternalTournamentName = e.ExternalTournamentName,
                    SpbhlTournamentId = e.SpbhlTournamentId,
                    SpbhlMatchId = e.SpbhlMatchId,
                    SpbhlMatchUrl = e.SpbhlMatchUrl,
                    HomeScore = e.HomeScore,
                    AwayScore = e.AwayScore,
                    GoalieNeededCount = e.GoalieRequest == null ? null : e.GoalieRequest.NeededCount,
                    GoalieConfirmedCount = e.GoalieRequest == null
                        ? null
                        : e.GoalieRequest.Applications.Count(a => a.Status == GoalieApplicationStatus.Confirmed),
                    GoalieApplicationStatus = e.GoalieRequest == null
                        ? null
                        : e.GoalieRequest.Applications
                            .Where(a => a.GoalieUserId == viewerUserId)
                            .Select(a => (GoalieApplicationStatus?)a.Status)
                            .FirstOrDefault(),
                    AttendanceStatus = e.Attendances
                        .Where(a => a.UserId == viewerUserId)
                        .Select(a => a.Status)
                        .FirstOrDefault()
                })
                .ToListAsync(cancellationToken);

            return new EventListDto { Events = events };
        }

        public async Task<EventDto> GetEvent(
            Guid eventId,
            Guid? viewerUserId,
            CancellationToken cancellationToken)
        {
            var eventAccess = await _context.Events
                .AsNoTracking()
                .Where(e => e.Id == eventId)
                .Select(e => new
                {
                    e.TeamId,
                    TeamVisibility = e.Team == null ? (TeamVisibility?)null : e.Team.Visibility
                })
                .FirstOrDefaultAsync(cancellationToken);

            if (eventAccess == null || !eventAccess.TeamId.HasValue || !eventAccess.TeamVisibility.HasValue)
                throw new NotFoundException("Событие не найдено");

            var goalieRestrictedView = false;

            if (eventAccess.TeamVisibility == TeamVisibility.Private)
            {
                if (!viewerUserId.HasValue)
                    throw new UnauthorizedException("Недостаточно прав для просмотра мероприятия");

                var isMember = await _context.TeamMemberships
                    .AsNoTracking()
                    .AnyAsync(
                        membership =>
                            membership.TeamId == eventAccess.TeamId.Value &&
                            membership.UserId == viewerUserId.Value,
                        cancellationToken);

                if (!isMember)
                {
                    if (!await CanGoalieReadEvent(eventId, viewerUserId.Value, cancellationToken))
                        throw new UnauthorizedException("Недостаточно прав для просмотра мероприятия");

                    goalieRestrictedView = true;
                }
            }

            IQueryable<ScheduledEvent> selectedEventQuery = _context.Events
                .AsNoTracking()
                .Include(e => e.Team);

            if (!goalieRestrictedView)
            {
                selectedEventQuery = selectedEventQuery
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
                        .ThenInclude(x => x.Exercise);
            }

            var selectedEvent = await selectedEventQuery.FirstOrDefaultAsync(
                e => e.Id == eventId,
                cancellationToken);

            if (selectedEvent == null || selectedEvent.Team == null)
                throw new NotFoundException("Событие не найдено");

            var teamJerseyNumbers = !goalieRestrictedView
                ? await _context.TeamMemberships
                    .AsNoTracking()
                    .Where(value => value.TeamId == eventAccess.TeamId.Value && value.TeamJerseyNumber.HasValue)
                    .ToDictionaryAsync(
                        value => value.UserId,
                        value => value.TeamJerseyNumber!.Value,
                        cancellationToken)
                : new Dictionary<Guid, int>();

            var attendances = goalieRestrictedView
                ? Enumerable.Empty<Attendance>()
                : selectedEvent.Attendances.Where(e => e.EventId == eventId);

            var attendanceDtos = new List<AttendanceLookUpDto>();
            foreach (var attend in attendances)
            {
                attendanceDtos.Add(new AttendanceLookUpDto()
                {
                    FirstName = attend.User.FirstName,
                    LastName = attend.User.LastName,
                    UserId = attend.User.Id,
                    Handedness = attend.User.Handedness,
                    JerseyNumber = teamJerseyNumbers.TryGetValue(attend.User.Id, out var teamNumber) ? teamNumber : attend.User.JerseyNumber,
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

            var lines = goalieRestrictedView
                ? Enumerable.Empty<Line>()
                : selectedEvent.Roster.Where(e => e.EventId == eventId);

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
                        JerseyNumber = !member.EventGuestId.HasValue && member.UserId.HasValue && teamJerseyNumbers.TryGetValue(member.UserId.Value, out var rosterTeamNumber)
                            ? rosterTeamNumber
                            : member.JerseyNumber,
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
                DurationMinutes = selectedEvent.DurationMinutes,
                Status = selectedEvent.Status,
                Title = selectedEvent.Title,
                Type = selectedEvent.Type,
                UpdatedAt = selectedEvent.UpdatedAt,
                Attendances = attendanceDtos,
                Roster = rosterDto,
                AwayTeamName = selectedEvent.AwayTeamName,
                LeagueName = selectedEvent.LeagueName,
                HomeTeamName = selectedEvent.HomeTeamName,
                UniformColorId = goalieRestrictedView ? null : selectedEvent.UniformColorId,
                TeamId = selectedEvent.TeamId,
                TeamName = selectedEvent.Team?.Name,
                ExternalLeagueProvider = selectedEvent.ExternalLeagueProvider,
                ExternalDivisionName = selectedEvent.ExternalDivisionName,
                ExternalTournamentName = selectedEvent.ExternalTournamentName,
                SpbhlTournamentId = selectedEvent.SpbhlTournamentId,
                SpbhlMatchId = selectedEvent.SpbhlMatchId,
                SpbhlMatchUrl = selectedEvent.SpbhlMatchUrl,
                HomeScore = selectedEvent.HomeScore,
                AwayScore = selectedEvent.AwayScore,
                UniformColor = goalieRestrictedView || selectedEvent.UniformColor == null
                    ? null
                    : new Shared.Models.UniformColors.UniformColorDto
                    {
                        Id = selectedEvent.UniformColor.Id,
                        Name = selectedEvent.UniformColor.Name,
                        ImageUrl = selectedEvent.UniformColor.ImageUrl,
                        TeamId = selectedEvent.UniformColor.TeamId
                    },
                Exercises = goalieRestrictedView
                    ? new List<Shared.Models.Exercises.ExerciseDto>()
                    : selectedEvent.ScheduledEventExercises
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

        public async Task<AttendanceLookUpDto> CreateEventGuest(
            Guid eventId,
            CreateEventGuestRequest dto,
            Guid actorUserId,
            CancellationToken cancellationToken)
        {
            var currentUser = await _context.Users.FirstOrDefaultAsync(
                u => u.Id == actorUserId,
                cancellationToken);
            if (currentUser == null)
                throw new NotFoundException("Пользователь не найден");

            var selectedEvent = await _context.Events.FirstOrDefaultAsync(
                e => e.Id == eventId,
                cancellationToken);
            if (selectedEvent == null)
                throw new NotFoundException("Событие не найдено");

            var canAccess = await CanAccessEventScope(
                selectedEvent.TeamId,
                actorUserId,
                cancellationToken);
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
                InvitedByUserId = actorUserId,
                FirstName = firstName,
                LastName = lastName,
                Handedness = dto.Handedness,
                JerseyNumber = dto.JerseyNumber,
                Status = AttendanceStatus.Confirmed,
                RespondedAt = now,
                CreatedAt = now,
                UpdatedAt = now,
            };

            await _context.EventGuests.AddAsync(guest, cancellationToken);
            await _context.SaveChangesAsync(cancellationToken);

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

        public async Task UpdateAttendance(
            Guid eventId,
            Guid targetUserId,
            UpdateAttendanceRequest dto,
            Guid actorUserId,
            CancellationToken cancellationToken)
        {
            var user = await _context.Users.FirstOrDefaultAsync(
                value => value.Id == targetUserId,
                cancellationToken);
            if (user == null)
                throw new NotFoundException("Пользователь не найден");

            var selectedEvent = await _context.Events
                .Include(value => value.Attendances)
                .FirstOrDefaultAsync(value => value.Id == eventId, cancellationToken);
            if (selectedEvent == null)
                throw new NotFoundException("Событие не найдено");

            if (actorUserId == targetUserId)
            {
                var canAccess = await CanAccessEventScope(
                    selectedEvent.TeamId,
                    actorUserId,
                    cancellationToken);
                if (!canAccess)
                    throw new UnauthorizedException("Недостаточно прав для изменения явки");
            }
            else
            {
                var canManage = await CanManageEventScope(
                    selectedEvent.TeamId,
                    actorUserId,
                    cancellationToken);
                if (!canManage)
                    throw new UnauthorizedException("Недостаточно прав для изменения чужой явки");
            }

            var attendance = selectedEvent.Attendances.FirstOrDefault(value => value.UserId == user.Id);
            var now = DateTime.UtcNow;

            if (attendance is null)
            {
                attendance = new Attendance()
                {
                    UserId = targetUserId,
                    CreatedAt = now,
                    Status = dto.Status,
                    Notes = dto.Notes,
                    UpdatedAt = now,
                    RespondedAt = now,
                    EventId = eventId,
                };
                await _context.Attendances.AddAsync(attendance, cancellationToken);
            }
            else
            {
                await _context.Attendances
                    .Where(value => value.EventId == eventId && value.UserId == targetUserId)
                    .ExecuteUpdateAsync(setters => setters
                        .SetProperty(value => value.Status, dto.Status)
                        .SetProperty(value => value.Notes, dto.Notes)
                        .SetProperty(value => value.RespondedAt, now)
                        .SetProperty(value => value.UpdatedAt, now),
                        cancellationToken);
            }

            var player = await _context.Players
                .Include(value => value.Line)
                .FirstOrDefaultAsync(
                    value => value.UserId == targetUserId && value.Line.EventId == eventId,
                    cancellationToken);

            if ((dto.Status == AttendanceStatus.Declined || dto.Status == AttendanceStatus.Pending) && player != null)
            {
                _context.Players.Remove(player);
            }

            await _context.SaveChangesAsync(cancellationToken);

            _logger.LogInformation(
                "Attendance updated. EventId={EventId}, UserId={UserId}, Status={Status}, RespondedAt={RespondedAt}",
                eventId,
                targetUserId,
                dto.Status,
                now);
        }

        public async Task UpdateEventGuestAttendance(
            Guid eventId,
            Guid guestId,
            UpdateAttendanceRequest dto,
            Guid actorUserId,
            CancellationToken cancellationToken)
        {
            var currentUser = await _context.Users.FirstOrDefaultAsync(
                value => value.Id == actorUserId,
                cancellationToken);
            if (currentUser == null)
                throw new NotFoundException("Пользователь не найден");

            var selectedEvent = await _context.Events.FirstOrDefaultAsync(
                value => value.Id == eventId,
                cancellationToken);
            if (selectedEvent == null)
                throw new NotFoundException("Событие не найдено");

            if (!selectedEvent.TeamId.HasValue)
                throw new UnauthorizedException("Недостаточно прав для изменения явки гостя");

            var guest = await _context.EventGuests.FirstOrDefaultAsync(
                value => value.Id == guestId && value.EventId == eventId,
                cancellationToken);
            if (guest == null)
                throw new NotFoundException("Гость не найден");

            var canManage = await CanManageEventScope(
                selectedEvent.TeamId,
                actorUserId,
                cancellationToken);
            if (!canManage && guest.InvitedByUserId != actorUserId)
                throw new UnauthorizedException("Недостаточно прав для изменения явки гостя");

            var now = DateTime.UtcNow;
            guest.Status = dto.Status;
            guest.Notes = dto.Notes;
            guest.RespondedAt = now;
            guest.UpdatedAt = now;

            var player = await _context.Players
                .Include(p => p.Line)
                .FirstOrDefaultAsync(
                    player => player.EventGuestId == guestId && player.Line.EventId == eventId,
                    cancellationToken);

            if ((dto.Status == AttendanceStatus.Declined || dto.Status == AttendanceStatus.Pending) && player != null)
            {
                _context.Players.Remove(player);
            }

            await _context.SaveChangesAsync(cancellationToken);

            _logger.LogInformation("Event guest attendance updated. EventId={EventId}, GuestId={GuestId}, Status={Status}, RespondedAt={RespondedAt}",
                eventId, guestId, dto.Status, now);
        }

        public async Task<bool> DeleteEvent(
            Guid eventId,
            Guid actorUserId,
            CancellationToken cancellationToken)
        {
            var user = await _context.Users.FirstOrDefaultAsync(
                value => value.Id == actorUserId,
                cancellationToken);

            if (user == null)
                throw new NotFoundException("Пользователь не найден");

            var eventTeamId = await _context.Events
                .AsNoTracking()
                .Where(value => value.Id == eventId)
                .Select(value => new { value.TeamId })
                .FirstOrDefaultAsync(cancellationToken);

            if (eventTeamId == null)
                throw new NotFoundException("Мероприятие не найдено");

            if (!eventTeamId.TeamId.HasValue)
                throw new UnauthorizedException("Недостаточно прав для удаления мероприятия");

            var canManage = await CanManageEventScope(
                eventTeamId.TeamId,
                actorUserId,
                cancellationToken);
            if (!canManage)
                throw new UnauthorizedException("Недостаточно прав для удаления мероприятия");

            var deletedRows = await _context.Events
                .Where(value => value.Id == eventId)
                .ExecuteDeleteAsync(cancellationToken);

            return deletedRows > 0;
        }

        private async Task<bool> CanManageEventScope(
            Guid? teamId,
            Guid actorUserId,
            CancellationToken cancellationToken)
        {
            if (!teamId.HasValue)
                return false;

            var teamExists = await _context.Teams
                .AsNoTracking()
                .AnyAsync(value => value.Id == teamId.Value, cancellationToken);

            if (!teamExists)
                throw new NotFoundException("Команда не найдена");

            return await _context.TeamMemberships
                .AsNoTracking()
                .AnyAsync(m =>
                    m.TeamId == teamId.Value &&
                    m.UserId == actorUserId &&
                    (m.Role == TeamMemberRole.Owner || m.Role == TeamMemberRole.Admin),
                    cancellationToken);
        }

        private async Task<bool> CanAccessEventScope(
            Guid? teamId,
            Guid actorUserId,
            CancellationToken cancellationToken)
        {
            if (!teamId.HasValue)
                return false;

            var teamExists = await _context.Teams
                .AsNoTracking()
                .AnyAsync(value => value.Id == teamId.Value, cancellationToken);

            if (!teamExists)
                throw new NotFoundException("Команда не найдена");

            return await _context.TeamMemberships
                .AsNoTracking()
                .AnyAsync(
                    value => value.TeamId == teamId.Value && value.UserId == actorUserId,
                    cancellationToken);
        }

        private async Task<bool> CanGoalieReadEvent(
            Guid eventId,
            Guid viewerUserId,
            CancellationToken cancellationToken)
        {
            var viewerIsGoalie = await _context.Users
                .AsNoTracking()
                .AnyAsync(
                    user => user.Id == viewerUserId && user.PrimaryPosition == Position.Goalie,
                    cancellationToken);

            if (!viewerIsGoalie)
                return false;

            // Existing applications keep status and notification links usable after the public request closes.
            return await _context.GoalieRequests
                .AsNoTracking()
                .AnyAsync(
                    request =>
                        request.EventId == eventId &&
                        ((request.Visibility == GoalieRequestVisibility.AllGoalies &&
                            request.Status == GoalieRequestStatus.Open) ||
                            request.Applications.Any(application => application.GoalieUserId == viewerUserId)),
                    cancellationToken);
        }
    }
}
