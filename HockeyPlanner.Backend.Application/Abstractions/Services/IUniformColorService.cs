using HockeyPlanner.Backend.Shared.Models.UniformColors;

namespace HockeyPlanner.Backend.Application.Abstractions.Services
{
    public interface IUniformColorService
    {
        Task EnsureCanCreate(Guid currentUserId);
        Task<UniformColorDto> Create(CreateUniformColorDto dto, Guid currentUserId);
        Task<IReadOnlyCollection<UniformColorDto>> GetAll();
    }
}
