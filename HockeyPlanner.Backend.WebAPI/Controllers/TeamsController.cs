using HockeyPlanner.Backend.Core.Entities;
using HockeyPlanner.Backend.Core.Enums;
using HockeyPlanner.Backend.Infrastructure.Data;
using HockeyPlanner.Backend.WebAPI.Models.Teams;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace HockeyPlanner.Backend.WebAPI.Controllers
{
    [ApiController]
    [Route("api/teams")]
    public class TeamsController : ControllerBase
    {
        private readonly AppDbContext _context;

        public TeamsController(AppDbContext context)
        {
            _context = context;
        }

        [HttpGet]
        public async Task<ActionResult<IReadOnlyCollection<TeamDto>>> GetMyTeams([FromQuery] Guid currentUserId)
        {
            if (currentUserId == Guid.Empty)
            {
                return BadRequest(new { message = "Параметр currentUserId обязателен." });
            }

            var userExists = await _context.Users.AsNoTracking().AnyAsync(user => user.Id == currentUserId);
            if (!userExists)
            {
                return NotFound(new { message = "Пользователь не найден." });
            }

            var teams = await _context.TeamMemberships
                .AsNoTracking()
                .Where(value => value.UserId == currentUserId)
                .OrderBy(value => value.Team.Name)
                .Select(value => new TeamDto
                {
                    Id = value.Team.Id,
                    Name = value.Team.Name,
                    Description = value.Team.Description,
                    Visibility = value.Team.Visibility,
                    InviteCode = value.Team.CreatedByUserId == currentUserId ? value.Team.InviteCode : string.Empty,
                    CreatedByUserId = value.Team.CreatedByUserId,
                    MembersCount = value.Team.Memberships.Count
                })
                .ToListAsync();

            return Ok(teams);
        }

        [HttpGet("public")]
        public async Task<ActionResult<IReadOnlyCollection<TeamDto>>> GetPublicTeams()
        {
            var teams = await _context.Teams
                .AsNoTracking()
                .Where(team => team.Visibility == TeamVisibility.Public)
                .OrderBy(team => team.Name)
                .Select(team => new TeamDto
                {
                    Id = team.Id,
                    Name = team.Name,
                    Description = team.Description,
                    Visibility = team.Visibility,
                    InviteCode = string.Empty,
                    CreatedByUserId = team.CreatedByUserId,
                    MembersCount = team.Memberships.Count
                })
                .ToListAsync();

            return Ok(teams);
        }

        [HttpGet("{id:guid}")]
        public async Task<ActionResult<TeamDto>> GetTeam(Guid id, [FromQuery] Guid? currentUserId)
        {
            var team = await _context.Teams
                .AsNoTracking()
                .Where(value => value.Id == id)
                .Select(value => new TeamDto
                {
                    Id = value.Id,
                    Name = value.Name,
                    Description = value.Description,
                    Visibility = value.Visibility,
                    InviteCode = currentUserId.HasValue && value.CreatedByUserId == currentUserId ? value.InviteCode : string.Empty,
                    CreatedByUserId = value.CreatedByUserId,
                    MembersCount = value.Memberships.Count
                })
                .FirstOrDefaultAsync();

            if (team == null)
            {
                return NotFound(new { message = "Команда не найдена." });
            }

            return Ok(team);
        }

        [HttpGet("{id:guid}/members")]
        public async Task<ActionResult<IReadOnlyCollection<TeamMemberDto>>> GetTeamMembers(Guid id)
        {
            var teamExists = await _context.Teams.AsNoTracking().AnyAsync(team => team.Id == id);
            if (!teamExists)
            {
                return NotFound(new { message = "Команда не найдена." });
            }

            var members = await _context.TeamMemberships
                .AsNoTracking()
                .Where(value => value.TeamId == id)
                .OrderBy(value => value.Role)
                .ThenBy(value => value.User.LastName)
                .ThenBy(value => value.User.FirstName)
                .Select(value => new TeamMemberDto
                {
                    UserId = value.UserId,
                    FirstName = value.User.FirstName,
                    LastName = value.User.LastName,
                    JerseyNumber = value.User.JerseyNumber,
                    Role = value.Role
                })
                .ToListAsync();

            return Ok(members);
        }

        [HttpPost]
        public async Task<ActionResult<TeamDto>> CreateTeam([FromBody] CreateTeamRequest request, [FromQuery] Guid currentUserId)
        {
            if (string.IsNullOrWhiteSpace(request.Name))
            {
                return BadRequest(new { message = "Название команды обязательно." });
            }

            var userExists = await _context.Users.AsNoTracking().AnyAsync(user => user.Id == currentUserId);
            if (!userExists)
            {
                return NotFound(new { message = "Пользователь не найден." });
            }

            var normalizedName = NormalizeName(request.Name);

            var duplicateExists = await _context.Teams
                .AsNoTracking()
                .AnyAsync(team => team.Name.ToLower() == normalizedName.ToLower());

            if (duplicateExists)
            {
                return Conflict(new { message = "Команда с таким названием уже существует." });
            }

            var inviteCode = await GenerateUniqueInviteCode();

            var team = new Team
            {
                Name = normalizedName,
                Description = NormalizeDescription(request.Description),
                Visibility = request.Visibility,
                InviteCode = inviteCode,
                CreatedByUserId = currentUserId,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            };

            var ownerMembership = new TeamMembership
            {
                Team = team,
                UserId = currentUserId,
                Role = TeamMemberRole.Owner,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            };

            await _context.Teams.AddAsync(team);
            await _context.TeamMemberships.AddAsync(ownerMembership);
            await _context.SaveChangesAsync();

            var dto = new TeamDto
            {
                Id = team.Id,
                Name = team.Name,
                Description = team.Description,
                Visibility = team.Visibility,
                InviteCode = team.InviteCode,
                CreatedByUserId = team.CreatedByUserId,
                MembersCount = 1
            };

            return CreatedAtAction(nameof(GetTeam), new { id = team.Id, currentUserId }, dto);
        }

        [HttpPost("join-by-code")]
        public async Task<ActionResult<TeamDto>> JoinByCode([FromBody] JoinTeamByCodeRequest request, [FromQuery] Guid currentUserId)
        {
            if (string.IsNullOrWhiteSpace(request.Code))
            {
                return BadRequest(new { message = "Код приглашения обязателен." });
            }

            var normalizedCode = request.Code.Trim().ToUpperInvariant();

            var team = await _context.Teams
                .Include(value => value.Memberships)
                .FirstOrDefaultAsync(value => value.InviteCode == normalizedCode);

            if (team == null)
            {
                return NotFound(new { message = "Команда с таким кодом не найдена." });
            }

            return await JoinTeamInternal(team, currentUserId);
        }

        [HttpPost("{id:guid}/join-public")]
        public async Task<ActionResult<TeamDto>> JoinPublic(Guid id, [FromQuery] Guid currentUserId)
        {
            var team = await _context.Teams
                .Include(value => value.Memberships)
                .FirstOrDefaultAsync(value => value.Id == id);

            if (team == null)
            {
                return NotFound(new { message = "Команда не найдена." });
            }

            if (team.Visibility != TeamVisibility.Public)
            {
                return BadRequest(new { message = "В приватную команду можно вступить только по коду." });
            }

            return await JoinTeamInternal(team, currentUserId);
        }

        private async Task<ActionResult<TeamDto>> JoinTeamInternal(Team team, Guid currentUserId)
        {
            var userExists = await _context.Users.AsNoTracking().AnyAsync(user => user.Id == currentUserId);
            if (!userExists)
            {
                return NotFound(new { message = "Пользователь не найден." });
            }

            var alreadyMember = team.Memberships.Any(value => value.UserId == currentUserId);
            if (alreadyMember)
            {
                return Ok(new TeamDto
                {
                    Id = team.Id,
                    Name = team.Name,
                    Description = team.Description,
                    Visibility = team.Visibility,
                    InviteCode = string.Empty,
                    CreatedByUserId = team.CreatedByUserId,
                    MembersCount = team.Memberships.Count
                });
            }

            var membership = new TeamMembership
            {
                TeamId = team.Id,
                UserId = currentUserId,
                Role = TeamMemberRole.Member,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            };

            await _context.TeamMemberships.AddAsync(membership);
            await _context.SaveChangesAsync();

            return Ok(new TeamDto
            {
                Id = team.Id,
                Name = team.Name,
                Description = team.Description,
                Visibility = team.Visibility,
                InviteCode = string.Empty,
                CreatedByUserId = team.CreatedByUserId,
                MembersCount = team.Memberships.Count + 1
            });
        }

        private async Task<string> GenerateUniqueInviteCode()
        {
            while (true)
            {
                var code = Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();
                var exists = await _context.Teams.AsNoTracking().AnyAsync(value => value.InviteCode == code);
                if (!exists)
                {
                    return code;
                }
            }
        }

        private static string NormalizeName(string value)
        {
            var parts = value
                .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            return string.Join(" ", parts);
        }

        private static string? NormalizeDescription(string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return null;
            }

            var normalized = NormalizeName(value);
            return normalized.Length > 1000 ? normalized[..1000] : normalized;
        }
    }
}
