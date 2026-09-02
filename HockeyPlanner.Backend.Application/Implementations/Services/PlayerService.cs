using HockeyPlanner.Backend.Application.Abstractions.Services;
using HockeyPlanner.Backend.Core.Enums;
using HockeyPlanner.Backend.Core.Exceptions;
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

        public async Task<bool> RemovePlayerById(
            Guid playerId,
            Guid actorUserId,
            CancellationToken cancellationToken)
        {
            var playerScope = await _context.Players
                .AsNoTracking()
                .Where(player => player.Id == playerId)
                .Select(player => new
                {
                    player.Line.Event.TeamId,
                    TeamExists = player.Line.Event.Team != null,
                })
                .FirstOrDefaultAsync(cancellationToken);

            if (playerScope == null)
                throw new NotFoundException("Игрок не найден");

            if (!playerScope.TeamId.HasValue)
                throw new UnauthorizedException("Недостаточно прав для удаления игрока");

            if (!playerScope.TeamExists)
                throw new NotFoundException("Команда не найдена");

            var canManageRoster = await _context.TeamMemberships
                .AsNoTracking()
                .AnyAsync(
                    membership =>
                        membership.TeamId == playerScope.TeamId.Value &&
                        membership.UserId == actorUserId &&
                        (membership.Role == TeamMemberRole.Owner || membership.Role == TeamMemberRole.Admin),
                    cancellationToken);

            if (!canManageRoster)
                throw new UnauthorizedException("Недостаточно прав для удаления игрока");

            var deletedRows = await _context.Players
                .Where(player => player.Id == playerId)
                .ExecuteDeleteAsync(cancellationToken);

            return deletedRows > 0;
        }
    }
}
