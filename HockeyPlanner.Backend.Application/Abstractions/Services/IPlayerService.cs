namespace HockeyPlanner.Backend.Application.Abstractions.Services
{
    public interface IPlayerService
    {
        Task<bool> RemovePlayerById(Guid playerId);
    }
}
