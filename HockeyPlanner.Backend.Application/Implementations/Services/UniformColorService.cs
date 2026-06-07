using HockeyPlanner.Backend.Application.Abstractions.Services;
using HockeyPlanner.Backend.Core.Entities;
using HockeyPlanner.Backend.Core.Enums;
using HockeyPlanner.Backend.Core.Exceptions;
using HockeyPlanner.Backend.Infrastructure.Data;
using HockeyPlanner.Backend.Shared;
using HockeyPlanner.Backend.Shared.Models.UniformColors;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace HockeyPlanner.Backend.Application.Implementations.Services
{
    internal class UniformColorService : IUniformColorService
    {
        private readonly AppDbContext _context;
        private readonly ILogger<UniformColorService> _logger;

        public UniformColorService(AppDbContext context, ILogger<UniformColorService> logger)
        {
            _context = context;
            _logger = logger;
        }

        public async Task EnsureCanCreate(Guid currentUserId, Guid teamId)
        {
            if (teamId == Guid.Empty)
                throw new BusinessRuleException("Команда для цвета формы обязательна");

            var currentUser = await _context.Users.FirstOrDefaultAsync(u => u.Id == currentUserId);
            if (currentUser == null)
                throw new NotFoundException("Пользователь не найден");

            var teamExists = await _context.Teams
                .AsNoTracking()
                .AnyAsync(team => team.Id == teamId);

            if (!teamExists)
                throw new NotFoundException("Команда не найдена");

            if (!await CanManageTeamUniformColors(currentUserId, teamId))
                throw new UnauthorizedException("Недостаточно прав для добавления цвета формы");
        }

        public async Task<UniformColorDto> Create(CreateUniformColorDto dto, Guid currentUserId)
        {
            await EnsureCanCreate(currentUserId, dto.TeamId);

            if (string.IsNullOrWhiteSpace(dto.Name))
                throw new BusinessRuleException("Название цвета формы обязательно");

            if (string.IsNullOrWhiteSpace(dto.ImageUrl))
                throw new BusinessRuleException("Ссылка на изображение цвета формы обязательна");

            var imageUrl = dto.ImageUrl.Trim();
            if (!Uri.IsWellFormedUriString(imageUrl, UriKind.Absolute))
                throw new BusinessRuleException("Ссылка на изображение должна быть корректным URL");

            var item = new UniformColor
            {
                Name = dto.Name.Trim(),
                ImageUrl = imageUrl,
                CreatedByUserId = currentUserId,
                TeamId = dto.TeamId,
                CreatedAt = DateTime.UtcNow,
            };

            await _context.UniformColors.AddAsync(item);
            await _context.SaveChangesAsync();

            _logger.LogInformation("Uniform color {UniformColorId} created by user {UserId}", item.Id, currentUserId);

            return new UniformColorDto
            {
                Id = item.Id,
                Name = item.Name,
                ImageUrl = item.ImageUrl,
                TeamId = item.TeamId,
            };
        }

        public async Task<IReadOnlyCollection<UniformColorDto>> GetAll(Guid teamId)
        {
            if (teamId == Guid.Empty)
                return Array.Empty<UniformColorDto>();

            var items = await _context.UniformColors
                .AsNoTracking()
                .Where(x => x.TeamId == teamId)
                .OrderBy(x => x.Name)
                .Select(x => new UniformColorDto
                {
                    Id = x.Id,
                    Name = x.Name,
                    ImageUrl = x.ImageUrl,
                    TeamId = x.TeamId
                })
                .ToListAsync();

            return items;
        }

        public async Task<UniformColorDto> Update(Guid id, UpdateUniformColorDto dto, Guid currentUserId)
        {
            var item = await _context.UniformColors.FirstOrDefaultAsync(x => x.Id == id);
            if (item == null)
                throw new NotFoundException("Цвет формы не найден");

            if (!item.TeamId.HasValue)
                throw new BusinessRuleException("Цвет формы не привязан к команде");

            if (!await CanManageTeamUniformColors(currentUserId, item.TeamId.Value))
                throw new UnauthorizedException("Недостаточно прав для редактирования цвета формы");

            if (string.IsNullOrWhiteSpace(dto.Name))
                throw new BusinessRuleException("Название цвета формы обязательно");

            if (string.IsNullOrWhiteSpace(dto.ImageUrl))
                throw new BusinessRuleException("Ссылка на изображение цвета формы обязательна");

            var imageUrl = dto.ImageUrl.Trim();
            if (!Uri.IsWellFormedUriString(imageUrl, UriKind.Absolute))
                throw new BusinessRuleException("Ссылка на изображение должна быть корректным URL");

            item.Name = dto.Name.Trim();
            item.ImageUrl = imageUrl;

            await _context.SaveChangesAsync();

            _logger.LogInformation("Uniform color {UniformColorId} updated by user {UserId}", item.Id, currentUserId);

            return new UniformColorDto
            {
                Id = item.Id,
                Name = item.Name,
                ImageUrl = item.ImageUrl,
                TeamId = item.TeamId,
            };
        }

        public async Task Delete(Guid id, Guid currentUserId)
        {
            var item = await _context.UniformColors.FirstOrDefaultAsync(x => x.Id == id);
            if (item == null)
                throw new NotFoundException("Цвет формы не найден");

            if (!item.TeamId.HasValue)
                throw new BusinessRuleException("Цвет формы не привязан к команде");

            if (!await CanManageTeamUniformColors(currentUserId, item.TeamId.Value))
                throw new UnauthorizedException("Недостаточно прав для удаления цвета формы");

            var events = await _context.Events
                .Where(x => x.UniformColorId == id)
                .ToListAsync();

            foreach (var scheduledEvent in events)
                scheduledEvent.UniformColorId = null;

            var lines = await _context.Lines
                .Where(x => x.UniformColorId == id)
                .ToListAsync();

            foreach (var line in lines)
                line.UniformColorId = null;

            _context.UniformColors.Remove(item);
            await _context.SaveChangesAsync();

            _logger.LogInformation("Uniform color {UniformColorId} deleted by user {UserId}", id, currentUserId);
        }

        private async Task<bool> CanManageTeamUniformColors(Guid currentUserId, Guid teamId)
        {

            return await _context.TeamMemberships
                .AsNoTracking()
                .AnyAsync(m =>
                    m.UserId == currentUserId &&
                    m.TeamId == teamId &&
                    (m.Role == TeamMemberRole.Owner || m.Role == TeamMemberRole.Admin));
        }
    }
}

