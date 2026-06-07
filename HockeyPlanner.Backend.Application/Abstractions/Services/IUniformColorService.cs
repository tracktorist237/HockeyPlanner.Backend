using HockeyPlanner.Backend.Shared.Models.UniformColors;

namespace HockeyPlanner.Backend.Application.Abstractions.Services
{
    public interface IUniformColorService
    {
        Task EnsureCanCreate(Guid currentUserId, Guid teamId);
        Task<UniformColorDto> Create(CreateUniformColorDto dto, Guid currentUserId);
        Task<UniformColorDto> Update(Guid id, UpdateUniformColorDto dto, Guid currentUserId);
        Task Delete(Guid id, Guid currentUserId);
        Task<IReadOnlyCollection<UniformColorDto>> GetAll(Guid teamId);
    }
}
