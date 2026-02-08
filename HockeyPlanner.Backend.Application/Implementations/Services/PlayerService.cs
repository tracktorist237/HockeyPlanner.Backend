using HockeyPlanner.Backend.Application.Abstractions.Services;
using HockeyPlanner.Backend.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace HockeyPlanner.Backend.Application.Implementations.Services
{
    internal class PlayerService : IPlayerService
    {
        private readonly AppDbContext _context;

        public PlayerService(AppDbContext context)
        {
            _context = context;
        }

        public async Task<bool> RemovePlayerById(Guid playerId)
        {
            var deletedRows = await _context.Players.Where(p => p.Id == playerId).ExecuteDeleteAsync();

            return deletedRows > 0;
        }
    }
}
