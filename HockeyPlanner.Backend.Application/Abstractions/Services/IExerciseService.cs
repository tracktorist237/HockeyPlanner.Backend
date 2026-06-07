using HockeyPlanner.Backend.Shared.Models.Exercises;

namespace HockeyPlanner.Backend.Application.Abstractions.Services
{
    public interface IExerciseService
    {
        Task<ExerciseDto> Create(CreateExerciseDto dto, Guid currentUserId);
        Task<ExerciseDto> Update(Guid id, UpdateExerciseDto dto, Guid currentUserId);
        Task Delete(Guid id, Guid currentUserId);
        Task<IReadOnlyCollection<ExerciseDto>> GetAll(Guid teamId);
    }
}

