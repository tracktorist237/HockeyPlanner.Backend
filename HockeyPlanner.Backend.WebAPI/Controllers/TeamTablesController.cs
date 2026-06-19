using HockeyPlanner.Backend.Core.Entities;
using HockeyPlanner.Backend.Core.Enums;
using HockeyPlanner.Backend.Infrastructure.Data;
using HockeyPlanner.Backend.WebAPI.Models.Tables;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace HockeyPlanner.Backend.WebAPI.Controllers
{
    [ApiController]
    public class TeamTablesController : ControllerBase
    {
        private readonly AppDbContext _context;

        public TeamTablesController(AppDbContext context)
        {
            _context = context;
        }

        [HttpGet("api/teams/{teamId:guid}/tables")]
        public async Task<ActionResult<IReadOnlyCollection<TeamTableSummaryDto>>> GetTeamTables(Guid teamId, [FromQuery] Guid currentUserId)
        {
            if (!await CanSeeTeamAsync(teamId, currentUserId))
            {
                return Forbid();
            }

            var canManage = await CanManageTeamAsync(teamId, currentUserId);
            var tables = await _context.TeamTables
                .AsNoTracking()
                .Where(value => value.TeamId == teamId)
                .OrderByDescending(value => value.CreatedAt)
                .Select(value => new TeamTableSummaryDto
                {
                    Id = value.Id,
                    TeamId = value.TeamId,
                    TeamName = value.Team.Name,
                    Name = value.Name,
                    TemplateType = value.TemplateType,
                    CreatedAt = value.CreatedAt,
                    RowsCount = value.Rows.Count,
                    CanManage = canManage
                })
                .ToListAsync();

            return Ok(tables);
        }

        [HttpGet("api/news/tables")]
        public async Task<ActionResult<IReadOnlyCollection<TeamTableSummaryDto>>> GetTablesFeed([FromQuery] Guid currentUserId)
        {
            if (currentUserId == Guid.Empty)
            {
                return BadRequest(new { message = "Параметр currentUserId обязателен." });
            }

            var manageableTeamIds = await _context.TeamMemberships
                .AsNoTracking()
                .Where(value => value.UserId == currentUserId && (value.Role == TeamMemberRole.Owner || value.Role == TeamMemberRole.Admin))
                .Select(value => value.TeamId)
                .ToListAsync();

            var tables = await _context.TeamTables
                .AsNoTracking()
                .Where(value => value.Team.Memberships.Any(membership => membership.UserId == currentUserId))
                .OrderBy(value => value.Team.Name)
                .ThenByDescending(value => value.CreatedAt)
                .Select(value => new TeamTableSummaryDto
                {
                    Id = value.Id,
                    TeamId = value.TeamId,
                    TeamName = value.Team.Name,
                    Name = value.Name,
                    TemplateType = value.TemplateType,
                    CreatedAt = value.CreatedAt,
                    RowsCount = value.Rows.Count,
                    CanManage = manageableTeamIds.Contains(value.TeamId)
                })
                .ToListAsync();

            return Ok(tables);
        }

        [HttpGet("api/teams/{teamId:guid}/tables/{tableId:guid}")]
        public async Task<ActionResult<TeamTableDto>> GetTeamTable(Guid teamId, Guid tableId, [FromQuery] Guid currentUserId)
        {
            if (!await CanSeeTeamAsync(teamId, currentUserId))
            {
                return Forbid();
            }

            await SyncTableRowsAsync(tableId, teamId);
            var table = await _context.TeamTables
                .AsNoTracking()
                .Include(value => value.Team)
                .Include(value => value.Rows)
                    .ThenInclude(value => value.User)
                .FirstOrDefaultAsync(value => value.Id == tableId && value.TeamId == teamId);

            if (table == null)
            {
                return NotFound(new { message = "Таблица не найдена." });
            }

            return Ok(ToTableDto(table, await CanManageTeamAsync(teamId, currentUserId)));
        }

        [HttpPost("api/teams/{teamId:guid}/tables")]
        public async Task<ActionResult<TeamTableDto>> CreateTeamTable(Guid teamId, [FromQuery] Guid currentUserId, [FromBody] CreateTeamTableRequest request)
        {
            if (!await CanManageTeamAsync(teamId, currentUserId))
            {
                return Forbid();
            }

            if (request.TemplateType != TeamTableTemplateType.PlayerStats)
            {
                return BadRequest(new { message = "Поддерживается только шаблон статистики игроков." });
            }

            var name = NormalizeName(request.Name);
            if (string.IsNullOrWhiteSpace(name))
            {
                name = "Статистика игроков";
            }

            var teamExists = await _context.Teams.AsNoTracking().AnyAsync(value => value.Id == teamId);
            if (!teamExists)
            {
                return NotFound(new { message = "Команда не найдена." });
            }

            var table = new TeamTable
            {
                TeamId = teamId,
                Name = name,
                TemplateType = request.TemplateType,
                CreatedByUserId = currentUserId,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            };

            var members = await _context.TeamMemberships
                .AsNoTracking()
                .Where(value => value.TeamId == teamId)
                .Select(value => value.UserId)
                .ToListAsync();

            foreach (var userId in members)
            {
                table.Rows.Add(new TeamTableRow
                {
                    UserId = userId,
                    CreatedAt = DateTime.UtcNow,
                    UpdatedAt = DateTime.UtcNow
                });
            }

            await _context.TeamTables.AddAsync(table);
            await _context.SaveChangesAsync();

            var created = await _context.TeamTables
                .AsNoTracking()
                .Include(value => value.Team)
                .Include(value => value.Rows)
                    .ThenInclude(value => value.User)
                .FirstAsync(value => value.Id == table.Id);

            return Ok(ToTableDto(created, true));
        }

        [HttpGet("api/events/{eventId:guid}/table-protocols")]
        public async Task<ActionResult<IReadOnlyCollection<EventTableProtocolDto>>> GetEventProtocols(Guid eventId, [FromQuery] Guid currentUserId)
        {
            var scheduledEvent = await _context.Events.AsNoTracking().FirstOrDefaultAsync(value => value.Id == eventId);
            if (scheduledEvent?.TeamId == null)
            {
                return NotFound(new { message = "Командное мероприятие не найдено." });
            }

            if (!await CanSeeTeamAsync(scheduledEvent.TeamId.Value, currentUserId))
            {
                return Forbid();
            }

            var canManage = await CanManageTeamAsync(scheduledEvent.TeamId.Value, currentUserId);
            var protocols = await _context.EventTableProtocols
                .AsNoTracking()
                .Include(value => value.Event)
                .Include(value => value.TeamTable)
                .Include(value => value.Rows)
                    .ThenInclude(value => value.User)
                .Where(value => value.EventId == eventId)
                .OrderByDescending(value => value.CreatedAt)
                .ToListAsync();

            return Ok(protocols.Select(value => ToProtocolDto(value, canManage)).ToList());
        }

        [HttpPost("api/events/{eventId:guid}/table-protocols")]
        public async Task<ActionResult<EventTableProtocolDto>> CreateEventProtocol(Guid eventId, [FromQuery] Guid currentUserId, [FromBody] CreateEventTableProtocolRequest request)
        {
            var scheduledEvent = await _context.Events.AsNoTracking().FirstOrDefaultAsync(value => value.Id == eventId);
            if (scheduledEvent?.TeamId == null)
            {
                return NotFound(new { message = "Командное мероприятие не найдено." });
            }

            if (!await CanManageTeamAsync(scheduledEvent.TeamId.Value, currentUserId))
            {
                return Forbid();
            }

            var table = await _context.TeamTables
                .FirstOrDefaultAsync(value => value.Id == request.TeamTableId && value.TeamId == scheduledEvent.TeamId.Value);

            if (table == null)
            {
                return NotFound(new { message = "Основная таблица не найдена." });
            }

            await SyncTableRowsAsync(table.Id, table.TeamId);
            await _context.Entry(table).Collection(value => value.Rows).LoadAsync();

            var existingProtocol = await _context.EventTableProtocols
                .AsNoTracking()
                .AnyAsync(value => value.EventId == eventId && value.TeamTable.TemplateType == table.TemplateType);

            if (existingProtocol)
            {
                return Conflict(new { message = "Для этого мероприятия уже есть протокол выбранного шаблона." });
            }

            var attendedUserIds = await _context.Attendances
                .AsNoTracking()
                .Where(value =>
                    value.EventId == eventId &&
                    (value.Status == AttendanceStatus.Confirmed || value.Status == AttendanceStatus.Late))
                .Select(value => value.UserId)
                .ToListAsync();
            var attendedUserIdSet = attendedUserIds.ToHashSet();

            var protocol = new EventTableProtocol
            {
                EventId = eventId,
                TeamTableId = table.Id,
                CreatedByUserId = currentUserId,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            };

            foreach (var row in table.Rows)
            {
                protocol.Rows.Add(new EventTableProtocolRow
                {
                    UserId = row.UserId,
                    Games = attendedUserIdSet.Contains(row.UserId) ? 1 : 0,
                    Goals = 0,
                    Assists = 0,
                    Points = 0,
                    CreatedAt = DateTime.UtcNow,
                    UpdatedAt = DateTime.UtcNow
                });
            }

            await _context.EventTableProtocols.AddAsync(protocol);
            await _context.SaveChangesAsync();
            await RecalculateTableAsync(table.Id);

            var created = await _context.EventTableProtocols
                .AsNoTracking()
                .Include(value => value.Event)
                .Include(value => value.TeamTable)
                .Include(value => value.Rows)
                    .ThenInclude(value => value.User)
                .FirstAsync(value => value.Id == protocol.Id);

            return Ok(ToProtocolDto(created, true));
        }

        [HttpPut("api/events/{eventId:guid}/table-protocols/{protocolId:guid}")]
        public async Task<ActionResult<EventTableProtocolDto>> UpdateProtocol(
            Guid eventId,
            Guid protocolId,
            [FromQuery] Guid currentUserId,
            [FromBody] UpdateEventTableProtocolRequest request)
        {
            var protocol = await _context.EventTableProtocols
                .Include(value => value.Event)
                .Include(value => value.TeamTable)
                .Include(value => value.Rows)
                    .ThenInclude(value => value.User)
                .FirstOrDefaultAsync(value => value.Id == protocolId && value.EventId == eventId);

            if (protocol?.Event.TeamId == null)
            {
                return NotFound(new { message = "Протокол не найден." });
            }

            if (!await CanManageTeamAsync(protocol.Event.TeamId.Value, currentUserId))
            {
                return Forbid();
            }

            var requestRows = request.Rows.ToDictionary(value => value.RowId);
            foreach (var row in protocol.Rows)
            {
                if (!requestRows.TryGetValue(row.Id, out var requestRow))
                {
                    continue;
                }

                row.Games = ClampStat(requestRow.Games);
                row.Goals = ClampStat(requestRow.Goals);
                row.Assists = ClampStat(requestRow.Assists);
                row.Points = row.Goals + row.Assists;
                row.UpdatedAt = DateTime.UtcNow;
            }

            protocol.UpdatedAt = DateTime.UtcNow;

            await _context.SaveChangesAsync();
            await RecalculateTableAsync(protocol.TeamTableId);

            return Ok(ToProtocolDto(protocol, true));
        }

        [HttpPut("api/events/{eventId:guid}/table-protocols/{protocolId:guid}/rows/{rowId:guid}")]
        public async Task<ActionResult<EventTableProtocolDto>> UpdateProtocolRow(
            Guid eventId,
            Guid protocolId,
            Guid rowId,
            [FromQuery] Guid currentUserId,
            [FromBody] UpdateEventTableProtocolRowRequest request)
        {
            var protocol = await _context.EventTableProtocols
                .Include(value => value.Event)
                .Include(value => value.TeamTable)
                .Include(value => value.Rows)
                    .ThenInclude(value => value.User)
                .FirstOrDefaultAsync(value => value.Id == protocolId && value.EventId == eventId);

            if (protocol?.Event.TeamId == null)
            {
                return NotFound(new { message = "Протокол не найден." });
            }

            if (!await CanManageTeamAsync(protocol.Event.TeamId.Value, currentUserId))
            {
                return Forbid();
            }

            var row = protocol.Rows.FirstOrDefault(value => value.Id == rowId);
            if (row == null)
            {
                return NotFound(new { message = "Строка протокола не найдена." });
            }

            row.Games = ClampStat(request.Games);
            row.Goals = ClampStat(request.Goals);
            row.Assists = ClampStat(request.Assists);
            row.Points = row.Goals + row.Assists;
            row.UpdatedAt = DateTime.UtcNow;
            protocol.UpdatedAt = DateTime.UtcNow;

            await _context.SaveChangesAsync();
            await RecalculateTableAsync(protocol.TeamTableId);

            return Ok(ToProtocolDto(protocol, true));
        }

        private async Task SyncTableRowsAsync(Guid tableId, Guid teamId)
        {
            var existingUserIds = await _context.TeamTableRows
                .Where(value => value.TeamTableId == tableId)
                .Select(value => value.UserId)
                .ToListAsync();

            var missingUserIds = await _context.TeamMemberships
                .AsNoTracking()
                .Where(value => value.TeamId == teamId && !existingUserIds.Contains(value.UserId))
                .Select(value => value.UserId)
                .ToListAsync();

            if (missingUserIds.Count == 0)
            {
                return;
            }

            foreach (var userId in missingUserIds)
            {
                await _context.TeamTableRows.AddAsync(new TeamTableRow
                {
                    TeamTableId = tableId,
                    UserId = userId,
                    CreatedAt = DateTime.UtcNow,
                    UpdatedAt = DateTime.UtcNow
                });
            }

            await _context.SaveChangesAsync();
        }

        private async Task RecalculateTableAsync(Guid tableId)
        {
            var rows = await _context.TeamTableRows
                .Where(value => value.TeamTableId == tableId)
                .ToListAsync();

            var totals = await _context.EventTableProtocolRows
                .AsNoTracking()
                .Where(value => value.EventTableProtocol.TeamTableId == tableId)
                .GroupBy(value => value.UserId)
                .Select(value => new
                {
                    UserId = value.Key,
                    Games = value.Sum(row => row.Games),
                    Goals = value.Sum(row => row.Goals),
                    Assists = value.Sum(row => row.Assists)
                })
                .ToDictionaryAsync(value => value.UserId);

            foreach (var row in rows)
            {
                if (totals.TryGetValue(row.UserId, out var total))
                {
                    row.Games = total.Games;
                    row.Goals = total.Goals;
                    row.Assists = total.Assists;
                    row.Points = total.Goals + total.Assists;
                }
                else
                {
                    row.Games = 0;
                    row.Goals = 0;
                    row.Assists = 0;
                    row.Points = 0;
                }

                row.UpdatedAt = DateTime.UtcNow;
            }

            await _context.SaveChangesAsync();
        }

        private async Task<bool> CanSeeTeamAsync(Guid teamId, Guid userId)
        {
            if (teamId == Guid.Empty || userId == Guid.Empty)
            {
                return false;
            }

            return await _context.TeamMemberships
                .AsNoTracking()
                .AnyAsync(value => value.TeamId == teamId && value.UserId == userId);
        }

        private async Task<bool> CanManageTeamAsync(Guid teamId, Guid userId)
        {
            if (teamId == Guid.Empty || userId == Guid.Empty)
            {
                return false;
            }

            return await _context.TeamMemberships
                .AsNoTracking()
                .AnyAsync(value =>
                    value.TeamId == teamId &&
                    value.UserId == userId &&
                    (value.Role == TeamMemberRole.Owner || value.Role == TeamMemberRole.Admin));
        }

        private static TeamTableDto ToTableDto(TeamTable table, bool canManage)
        {
            return new TeamTableDto
            {
                Id = table.Id,
                TeamId = table.TeamId,
                TeamName = table.Team.Name,
                Name = table.Name,
                TemplateType = table.TemplateType,
                CreatedAt = table.CreatedAt,
                CanManage = canManage,
                Rows = table.Rows
                    .OrderByDescending(value => value.Points)
                    .ThenBy(value => value.Games)
                    .ThenByDescending(value => value.Goals)
                    .ThenBy(value => value.User.LastName)
                    .ThenBy(value => value.User.FirstName)
                    .Select(ToTableRowDto)
                    .ToList()
            };
        }

        private static TeamTableRowDto ToTableRowDto(TeamTableRow row)
        {
            return new TeamTableRowDto
            {
                Id = row.Id,
                UserId = row.UserId,
                PlayerName = $"{row.User.LastName} {row.User.FirstName}".Trim(),
                JerseyNumber = row.User.JerseyNumber,
                PhotoUrl = row.User.PhotoUrl,
                Games = row.Games,
                Goals = row.Goals,
                Assists = row.Assists,
                Points = row.Points
            };
        }

        private static EventTableProtocolDto ToProtocolDto(EventTableProtocol protocol, bool canManage)
        {
            return new EventTableProtocolDto
            {
                Id = protocol.Id,
                EventId = protocol.EventId,
                EventTitle = protocol.Event.Title,
                TeamTableId = protocol.TeamTableId,
                TeamTableName = protocol.TeamTable.Name,
                CreatedAt = protocol.CreatedAt,
                CanManage = canManage,
                Rows = protocol.Rows
                    .OrderBy(value => value.User.LastName)
                    .ThenBy(value => value.User.FirstName)
                    .Select(value => new EventTableProtocolRowDto
                    {
                        Id = value.Id,
                        UserId = value.UserId,
                        PlayerName = $"{value.User.LastName} {value.User.FirstName}".Trim(),
                        JerseyNumber = value.User.JerseyNumber,
                        PhotoUrl = value.User.PhotoUrl,
                        Games = value.Games,
                        Goals = value.Goals,
                        Assists = value.Assists,
                        Points = value.Points
                    })
                    .ToList()
            };
        }

        private static int ClampStat(int value)
        {
            return Math.Clamp(value, 0, 999);
        }

        private static string NormalizeName(string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return string.Empty;
            }

            var parts = value
                .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            var normalized = string.Join(" ", parts);
            return normalized.Length > 120 ? normalized[..120] : normalized;
        }
    }
}
