using HockeyPlanner.Backend.Shared.Models.Exercises;

namespace HockeyPlanner.Backend.Application.Abstractions.Services
{
    public interface IExerciseService
    {
        Task<ExerciseDto> Create(CreateExerciseDto dto, Guid currentUserId);
        Task<IReadOnlyCollection<ExerciseDto>> GetAll();
    }
}

