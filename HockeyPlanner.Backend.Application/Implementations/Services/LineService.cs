using HockeyPlanner.Backend.Application.Abstractions.Services;
using HockeyPlanner.Backend.Core.Entities;
using HockeyPlanner.Backend.Infrastructure.Data;
using HockeyPlanner.Backend.Shared.Models.Events;
using HockeyPlanner.Backend.Shared.Models.Lines;
using Microsoft.EntityFrameworkCore;

namespace HockeyPlanner.Backend.Application.Implementations.Services
{
    internal class LineService : ILineService
    {
        private readonly AppDbContext _context;

        public LineService(AppDbContext context)
        {
            _context = context;
        }

        public async Task<List<LineDto>> GetRosterByEvent(Guid eventId)
        {
            var lines = await _context.Lines
                .AsNoTracking()
                .Include(l => l.Players)
                .Where(l => l.EventId == eventId)
                .ToListAsync();

            var result = lines.Select(line => MapToLineDto(line)).ToList();

            return result;
        }

        public async Task<List<LineDto>> CreateRoster(CreateRosterRequest request)
        {
            var result = new List<LineDto>();
            var userIds = request.Lines.Select(l => l.Players.Select(p => p.UserId)).SelectMany(e => e).ToList();
            var usersData = await _context.Users.AsNoTracking().Where(u => userIds.Contains(u.Id)).ToListAsync();
            var lines = new List<Line>();

            foreach (var lineData in request.Lines)
            {
                var line = new Line()
                {
                    Name = lineData.Name,
                    Order = lineData.Order,
                    CreatedAt = DateTime.UtcNow,
                    EventId = request.EventId,
                };
                var players = new List<Player>();

                foreach (var playerData in lineData.Players)
                {
                    var userData = usersData.FirstOrDefault(u => u.Id == playerData.UserId);
                    players.Add(new Player()
                    {
                        CreatedAt = DateTime.UtcNow,
                        FirstName = userData.FirstName,
                        LastName = userData.LastName,
                        JerseyNumber = userData.JerseyNumber,
                        Handedness = userData.Handedness,
                        LineId = line.Id,
                        Role = playerData.Role,
                        UserId = userData.Id,
                    });
                }
                line.Players = players;

                lines.Add(line);
            }

            await _context.Lines.AddRangeAsync(lines);
            await _context.SaveChangesAsync();

            result = lines.Select(line => MapToLineDto(line)).ToList();

            return result;
        }

        public async Task<bool> RemoveRosterByEvent(Guid eventId)
        {
            var deletedRows = 0;
            var lineIds = await _context.Lines
                .Where(l => l.EventId == eventId)
                .Select(l => l.Id)
                .ToListAsync();

            if (lineIds.Any())
            {
                deletedRows += await _context.Players
                    .Where(p => lineIds.Contains(p.LineId))
                    .ExecuteDeleteAsync();

                deletedRows += await _context.Lines
                    .Where(l => l.EventId == eventId)
                    .ExecuteDeleteAsync();
            }

            return deletedRows > 0;
        }

        private LineDto MapToLineDto(Line line)
        {
            return new LineDto()
            {
                Id = line.Id,
                Name = line.Name,
                Order = line.Order,
                Members = line.Players
                    .Select(p => new PlayerLookUpDto()
                    {
                        FirstName = p.FirstName,
                        JerseyNumber = p.JerseyNumber,
                        LastName = p.LastName,
                        Role = p.Role,
                        UserId = p.UserId
                    })
                    .ToList(),
            };
        }
    }
}
