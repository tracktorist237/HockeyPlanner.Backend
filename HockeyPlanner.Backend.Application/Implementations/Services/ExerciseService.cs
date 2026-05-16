using HockeyPlanner.Backend.Application.Abstractions.Services;
using HockeyPlanner.Backend.Core.Entities;
using HockeyPlanner.Backend.Core.Enums;
using HockeyPlanner.Backend.Core.Exceptions;
using HockeyPlanner.Backend.Infrastructure.Data;
using HockeyPlanner.Backend.Shared;
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
            var currentUser = await _context.Users.FirstOrDefaultAsync(u => u.Id == currentUserId);
            if (currentUser == null)
                throw new NotFoundException("Пользователь не найден");

            if (!await CanManageGlobalDictionaries(currentUserId))
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
                CreatedAt = DateTime.UtcNow,
            };

            await _context.Exercises.AddAsync(exercise);
            await _context.SaveChangesAsync();

            _logger.LogInformation("Exercise {ExerciseId} created by user {UserId}", exercise.Id, currentUserId);

            return new ExerciseDto
            {
                Id = exercise.Id,
                Name = exercise.Name,
                VideoUrl = exercise.VideoUrl
            };
        }

        public async Task<IReadOnlyCollection<ExerciseDto>> GetAll()
        {
            var items = await _context.Exercises
                .AsNoTracking()
                .OrderBy(x => x.Name)
                .Select(x => new ExerciseDto
                {
                    Id = x.Id,
                    Name = x.Name,
                    VideoUrl = x.VideoUrl
                })
                .ToListAsync();

            return items;
        }

        private async Task<bool> CanManageGlobalDictionaries(Guid currentUserId)
        {

            return await _context.TeamMemberships
                .AsNoTracking()
                .AnyAsync(m =>
                    m.UserId == currentUserId &&
                    (m.Role == TeamMemberRole.Owner || m.Role == TeamMemberRole.Admin));
        }
    }
}

