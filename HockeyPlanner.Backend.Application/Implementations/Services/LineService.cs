using HockeyPlanner.Backend.Application.Abstractions.Services;
using HockeyPlanner.Backend.Core.Entities;
using HockeyPlanner.Backend.Core.Enums;
using HockeyPlanner.Backend.Core.Exceptions;
using HockeyPlanner.Backend.Infrastructure.Data;
using HockeyPlanner.Backend.Shared;
using HockeyPlanner.Backend.Shared.Models.Events;
using HockeyPlanner.Backend.Shared.Models.Lines;
using HockeyPlanner.Backend.Shared.Models.UniformColors;
using Microsoft.EntityFrameworkCore;

namespace HockeyPlanner.Backend.Application.Implementations.Services
{
    internal class LineService : ILineService
    {
        private readonly AppDbContext _context;
        private readonly INotificationService _notificationService;

        public LineService(AppDbContext context, INotificationService notificationService)
        {
            _context = context;
            _notificationService = notificationService;
        }

        public async Task<List<LineDto>> GetRosterByEvent(Guid eventId)
        {
            var lines = await _context.Lines
                .AsNoTracking()
                .Include(l => l.Players)
                    .ThenInclude(p => p.EventGuest)
                .Include(l => l.UniformColor)
                .Where(l => l.EventId == eventId)
                .ToListAsync();

            var result = lines.Select(line => MapToLineDto(line)).ToList();

            return result;
        }

        public async Task<List<LineDto>> CreateRoster(CreateUpdateRosterRequest request, Guid currentUserId)
        {
            var result = new List<LineDto>();
            var userIds = request.Lines
                .SelectMany(l => l.Players)
                .Where(p => !p.IsGuest)
                .Select(p => p.UserId)
                .Distinct()
                .ToList();
            var guestIds = request.Lines
                .SelectMany(l => l.Players)
                .Where(p => p.IsGuest)
                .Select(p => p.UserId)
                .Distinct()
                .ToList();
            var usersData = await _context.Users.AsNoTracking().Where(u => userIds.Contains(u.Id)).ToListAsync();
            var guestsData = await _context.EventGuests.AsNoTracking().Where(g => guestIds.Contains(g.Id) && g.EventId == request.EventId).ToListAsync();
            var lines = new List<Line>();
            var eventInfo = await _context.Events
                .AsNoTracking()
                .Where(e => e.Id == request.EventId)
                .Select(e => new { e.Type, e.TeamId })
                .FirstOrDefaultAsync();

            if (eventInfo == null)
                throw new NotFoundException("Мероприятие не найдено");

            var currentUser = await _context.Users.FirstOrDefaultAsync(u => u.Id == currentUserId);
            if (currentUser == null)
                throw new NotFoundException("Пользователь не найден");

            //Проверка прав
            var hasPermission = await CanManageEventRoster(request.EventId, currentUserId);
            if (!hasPermission)
                throw new UnauthorizedException("Недостаточно прав для обновления мероприятия");

            var requestedUniformColorIds = request.Lines
                .Where(line => line.UniformColorId.HasValue)
                .Select(line => line.UniformColorId!.Value)
                .Distinct()
                .ToList();

            if (eventInfo.Type != EventType.Practice && requestedUniformColorIds.Count > 0)
                throw new BusinessRuleException("Цвет формы для звена доступен только для тренировки");

            if (requestedUniformColorIds.Count > 0)
            {
                if (!eventInfo.TeamId.HasValue)
                    throw new BusinessRuleException("Команда обязательна для выбора цвета формы звена");

                var validUniformColorIds = await _context.UniformColors
                    .AsNoTracking()
                    .Where(color => color.TeamId == eventInfo.TeamId.Value && requestedUniformColorIds.Contains(color.Id))
                    .Select(color => color.Id)
                    .ToListAsync();

                if (validUniformColorIds.Count != requestedUniformColorIds.Count)
                    throw new BusinessRuleException("Некоторые цвета формы не найдены для этой команды");
            }

            foreach (var lineData in request.Lines)
            {
                var line = new Line()
                {
                    Name = lineData.Name,
                    Order = lineData.Order,
                    UniformColorId = eventInfo.Type == EventType.Practice ? lineData.UniformColorId : null,
                    CreatedAt = DateTime.UtcNow,
                    EventId = request.EventId,
                };
                var players = new List<Player>();

                foreach (var playerData in lineData.Players)
                {
                    if (playerData.IsGuest)
                    {
                        var guestData = guestsData.FirstOrDefault(g => g.Id == playerData.UserId);
                        if (guestData == null)
                            throw new NotFoundException("Гость мероприятия не найден");

                        players.Add(new Player()
                        {
                            CreatedAt = DateTime.UtcNow,
                            FirstName = guestData.FirstName,
                            LastName = guestData.LastName,
                            JerseyNumber = guestData.JerseyNumber,
                            Handedness = guestData.Handedness,
                            LineId = line.Id,
                            Role = playerData.Role,
                            EventGuestId = guestData.Id,
                        });
                    }
                    else
                    {
                        var userData = usersData.FirstOrDefault(u => u.Id == playerData.UserId);
                        if (userData == null)
                            throw new NotFoundException("Пользователь не найден");

                        players.Add(new Player()
                        {
                            CreatedAt = DateTime.UtcNow,
                            FirstName = userData.FirstName,
                            LastName = userData.LastName,
                            JerseyNumber = userData.JerseyNumber,
                            Handedness = userData.Handedness,
                            LineId = line.Id,
                            Role = playerData.Role,
                            UserId = userData.Id,
                        });
                    }
                }
                line.Players = players;

                lines.Add(line);
            }

            await _context.Lines.AddRangeAsync(lines);
            await _context.SaveChangesAsync();

            result = lines.Select(line => MapToLineDto(line)).ToList();

            return result;
        }

        public async Task<bool> RemoveRosterByEvent(Guid eventId, Guid currentUserId)
        {
            var currentUser = await _context.Users.FirstOrDefaultAsync(u => u.Id == currentUserId);
            if (currentUser == null)
                throw new NotFoundException("Пользователь не найден");

            //Проверка прав
            var hasPermission = await CanManageEventRoster(eventId, currentUserId);
            if (!hasPermission)
                throw new UnauthorizedException("Недостаточно прав для обновления мероприятия");

            var deletedRows = 0;
            var lineIds = await _context.Lines
                .Where(l => l.EventId == eventId)
                .Select(l => l.Id)
                .ToListAsync();

            if (lineIds.Any())
            {
                deletedRows += await _context.Players
                    .Where(p => lineIds.Contains(p.LineId))
                    .ExecuteDeleteAsync();

                deletedRows += await _context.Lines
                    .Where(l => l.EventId == eventId)
                    .ExecuteDeleteAsync();
            }

            return deletedRows > 0;
        }

        public async Task<List<LineDto>> UpdateRoster(CreateUpdateRosterRequest request, Guid currentUserId)
        {
            var currentUser = await _context.Users.FirstOrDefaultAsync(u => u.Id == currentUserId);
            if (currentUser == null)
                throw new NotFoundException("Пользователь не найден");

            //Проверка прав
            var hasPermission = await CanManageEventRoster(request.EventId, currentUserId);
            if (!hasPermission)
                throw new UnauthorizedException("Недостаточно прав для обновления мероприятия");

            await RemoveRosterByEvent(request.EventId, currentUserId);

            var result = await CreateRoster(request, currentUserId);

            var eventInfo = await _context.Events
                .AsNoTracking()
                .Where(e => e.Id == request.EventId)
                .Select(e => new { e.Id, e.Title })
                .FirstOrDefaultAsync();

            if (eventInfo != null && result.Count > 0)
            {
                var userIds = result
                    .SelectMany(line => line.Members)
                    .Where(member => !member.IsGuest)
                    .Select(member => member.UserId)
                    .Distinct()
                    .ToList();

                await _notificationService.NotifyUsersAsync(
                    userIds,
                    NotificationType.EventRosterReady,
                    NotificationCategory.RosterReady,
                    "Состав готов",
                    $"Состав на событие \"{eventInfo.Title}\" готов. Посмотрите своё звено.",
                    $"/events/{eventInfo.Id}?tab=roster");
            }

            return result;
        }

        private LineDto MapToLineDto(Line line)
        {
            return new LineDto()
            {
                Id = line.Id,
                Name = line.Name,
                Order = line.Order,
                UniformColorId = line.UniformColorId,
                UniformColor = line.UniformColor == null
                    ? null
                    : new UniformColorDto
                    {
                        Id = line.UniformColor.Id,
                        Name = line.UniformColor.Name,
                        ImageUrl = line.UniformColor.ImageUrl,
                        TeamId = line.UniformColor.TeamId
                    },
                Members = line.Players
                    .Select(p => new PlayerLookUpDto()
                    {
                        FirstName = p.FirstName,
                        JerseyNumber = p.JerseyNumber,
                        LastName = p.LastName,
                        Role = p.Role,
                        UserId = p.EventGuestId ?? p.UserId!.Value,
                        PlayerId = p.Id,
                        IsGuest = p.EventGuestId.HasValue,
                        InvitedByUserId = p.EventGuest == null ? null : p.EventGuest.InvitedByUserId,
                    })
                    .ToList(),
            };
        }

        private async Task<bool> CanManageEventRoster(Guid eventId, Guid currentUserId)
        {
            var eventTeamId = await _context.Events
                .AsNoTracking()
                .Where(e => e.Id == eventId)
                .Select(e => e.TeamId)
                .FirstOrDefaultAsync();

            if (!eventTeamId.HasValue)
                return false;

            return await _context.TeamMemberships
                .AsNoTracking()
                .AnyAsync(m =>
                    m.TeamId == eventTeamId.Value &&
                    m.UserId == currentUserId &&
                    (m.Role == TeamMemberRole.Owner || m.Role == TeamMemberRole.Admin));
        }
    }
}
