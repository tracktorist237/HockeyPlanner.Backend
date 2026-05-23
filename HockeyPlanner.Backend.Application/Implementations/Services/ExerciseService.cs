using HockeyPlanner.Backend.Application.Abstractions.Services;
using HockeyPlanner.Backend.Core.Entities;
using HockeyPlanner.Backend.Core.Enums;
using HockeyPlanner.Backend.Core.Exceptions;
using HockeyPlanner.Backend.Infrastructure.Data;
using HockeyPlanner.Backend.Shared.Models.Exercises;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace HockeyPlanner.Backend.Application.Implementations.Services
{
    internal class ExerciseService : IExerciseService
    {
        private readonly AppDbContext _context;
        private readonly ILogger<ExerciseService> _logger;

        public ExerciseService(AppDbContext context, ILogger<ExerciseService> logger)
        {
            _context = context;
            _logger = logger;
        }

        public async Task<ExerciseDto> Create(CreateExerciseDto dto, Guid currentUserId)
        {
            if (dto.TeamId == Guid.Empty)
                throw new BusinessRuleException("Команда обязательна для упражнения");

            var currentUser = await _context.Users.FirstOrDefaultAsync(u => u.Id == currentUserId);
            if (currentUser == null)
                throw new NotFoundException("Пользователь не найден");

            var teamExists = await _context.Teams.AnyAsync(t => t.Id == dto.TeamId);
            if (!teamExists)
                throw new NotFoundException("Команда не найдена");

            if (!await CanManageTeamExercises(currentUserId, dto.TeamId))
                throw new UnauthorizedException("Недостаточно прав для добавления упражнения");

            if (string.IsNullOrWhiteSpace(dto.Name))
                throw new BusinessRuleException("Название упражнения обязательно");

            if (string.IsNullOrWhiteSpace(dto.VideoUrl))
                throw new BusinessRuleException("Ссылка на видео обязательна");

            var exercise = new Exercise
            {
                Name = dto.Name.Trim(),
                VideoUrl = dto.VideoUrl.Trim(),
                CreatedByUserId = currentUserId,
                TeamId = dto.TeamId,
                CreatedAt = DateTime.UtcNow,
            };

            await _context.Exercises.AddAsync(exercise);
            await _context.SaveChangesAsync();

            _logger.LogInformation("Exercise {ExerciseId} created by user {UserId}", exercise.Id, currentUserId);

            return new ExerciseDto
            {
                Id = exercise.Id,
                Name = exercise.Name,
                VideoUrl = exercise.VideoUrl,
                TeamId = exercise.TeamId
            };
        }

        public async Task<IReadOnlyCollection<ExerciseDto>> GetAll(Guid teamId)
        {
            if (teamId == Guid.Empty)
                return Array.Empty<ExerciseDto>();

            var items = await _context.Exercises
                .AsNoTracking()
                .Where(x => x.TeamId == teamId)
                .OrderBy(x => x.Name)
                .Select(x => new ExerciseDto
                {
                    Id = x.Id,
                    Name = x.Name,
                    VideoUrl = x.VideoUrl,
                    TeamId = x.TeamId
                })
                .ToListAsync();

            return items;
        }

        private async Task<bool> CanManageTeamExercises(Guid currentUserId, Guid teamId)
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
