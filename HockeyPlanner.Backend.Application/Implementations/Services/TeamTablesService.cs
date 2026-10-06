using HockeyPlanner.Backend.Core.Entities;
using HockeyPlanner.Backend.Core.Enums;
using HockeyPlanner.Backend.Infrastructure.Data;
using HockeyPlanner.Backend.Shared.Models.Tables;
using HockeyPlanner.Backend.Application.Abstractions.Services;
using HockeyPlanner.Backend.Application.Policies;
using HockeyPlanner.Backend.Core.Exceptions;
using Microsoft.EntityFrameworkCore;

namespace HockeyPlanner.Backend.Application.Implementations.Services
{
    internal sealed class TeamTablesService : ITeamTablesService
    {
        private readonly AppDbContext _context;

        private readonly TimeProvider _timeProvider;

        public TeamTablesService(AppDbContext context, TimeProvider timeProvider)
        {
            _context = context;
            _timeProvider = timeProvider;
        }

        public async Task<IReadOnlyCollection<TeamTableSummaryDto>> GetTeamTables(Guid teamId, Guid actorUserId, CancellationToken cancellationToken)
        {
            var role = await GetTeamRoleAsync(teamId, actorUserId, cancellationToken);
            if (!role.HasValue)
            {
                throw new UnauthorizedException("Недостаточно прав");
            }

            var canManage = CoreTeamRolePolicy.CanManage(role);
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
                .ToListAsync(cancellationToken);

            return tables;
        }

        public async Task<IReadOnlyCollection<TeamTableSummaryDto>> GetTablesFeed(Guid actorUserId, CancellationToken cancellationToken)
        {
            var memberships = await _context.TeamMemberships.AsNoTracking()
                .Where(value => value.UserId == actorUserId)
                .Select(value => new { value.TeamId, value.Role }).ToListAsync(cancellationToken);
            var manageableTeamIds = memberships.Where(value => CoreTeamRolePolicy.CanManage(value.Role))
                .Select(value => value.TeamId).ToArray();

            var tables = await _context.TeamTables
                .AsNoTracking()
                .Where(value => value.Team.Memberships.Any(membership => membership.UserId == actorUserId))
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
                .ToListAsync(cancellationToken);

            return tables;
        }

        public async Task<TeamTableDto> GetTeamTable(Guid teamId, Guid tableId, Guid actorUserId, CancellationToken cancellationToken)
        {
            var role = await GetTeamRoleAsync(teamId, actorUserId, cancellationToken);
            if (!role.HasValue)
            {
                throw new UnauthorizedException("Недостаточно прав");
            }

            if (!await _context.TeamTables.AsNoTracking().AnyAsync(value => value.Id == tableId && value.TeamId == teamId, cancellationToken))
                throw new NotFoundException("Таблица не найдена.");

            await SyncTableRowsAsync(tableId, teamId, cancellationToken);
            var table = await _context.TeamTables
                .AsNoTracking()
                .Include(value => value.Team)
                .Include(value => value.Rows)
                    .ThenInclude(value => value.User)
                .FirstOrDefaultAsync(value => value.Id == tableId && value.TeamId == teamId, cancellationToken);

            if (table == null)
            {
                throw new NotFoundException("Таблица не найдена.");
            }

            return ToTableDto(table, CoreTeamRolePolicy.CanManage(role), await GetTeamJerseyNumbersAsync(teamId, cancellationToken));
        }

        public async Task<TeamTableDto> CreateTeamTable(Guid teamId, Guid actorUserId, CreateTeamTableRequest request, CancellationToken cancellationToken)
        {
            if (!await CanManageTeamAsync(teamId, actorUserId, cancellationToken))
            {
                throw new UnauthorizedException("Недостаточно прав");
            }

            if (request.TemplateType != TeamTableTemplateType.PlayerStats)
            {
                throw new BusinessRuleException("Поддерживается только шаблон статистики игроков.");
            }

            var name = NormalizeName(request.Name);
            if (string.IsNullOrWhiteSpace(name))
            {
                name = "Статистика игроков";
            }

            var table = new TeamTable
            {
                TeamId = teamId,
                Name = name,
                TemplateType = request.TemplateType,
                CreatedByUserId = actorUserId,
                CreatedAt = _timeProvider.GetUtcNow().UtcDateTime,
                UpdatedAt = _timeProvider.GetUtcNow().UtcDateTime
            };

            var members = await _context.TeamMemberships
                .AsNoTracking()
                .Where(value => value.TeamId == teamId)
                .Select(value => value.UserId)
                .ToListAsync(cancellationToken);

            foreach (var userId in members)
            {
                table.Rows.Add(new TeamTableRow
                {
                    UserId = userId,
                    CreatedAt = _timeProvider.GetUtcNow().UtcDateTime,
                    UpdatedAt = _timeProvider.GetUtcNow().UtcDateTime
                });
            }

            await _context.TeamTables.AddAsync(table, cancellationToken);
            await _context.SaveChangesAsync(cancellationToken);

            var created = await _context.TeamTables
                .AsNoTracking()
                .Include(value => value.Team)
                .Include(value => value.Rows)
                    .ThenInclude(value => value.User)
                .FirstAsync(value => value.Id == table.Id, cancellationToken);

            return ToTableDto(created, true, await GetTeamJerseyNumbersAsync(teamId, cancellationToken));
        }

        public async Task<IReadOnlyCollection<EventTableProtocolDto>> GetEventProtocols(Guid eventId, Guid actorUserId, CancellationToken cancellationToken)
        {
            var scheduledEvent = await _context.Events.AsNoTracking().FirstOrDefaultAsync(value => value.Id == eventId, cancellationToken);
            if (scheduledEvent?.TeamId == null)
            {
                throw new NotFoundException("Командное мероприятие не найдено.");
            }

            var role = await GetTeamRoleAsync(scheduledEvent.TeamId.Value, actorUserId, cancellationToken);
            if (!role.HasValue)
            {
                throw new UnauthorizedException("Недостаточно прав");
            }

            var canManage = CoreTeamRolePolicy.CanManage(role);
            var protocols = await _context.EventTableProtocols
                .AsNoTracking()
                .Include(value => value.Event)
                .Include(value => value.TeamTable)
                .Include(value => value.Rows)
                    .ThenInclude(value => value.User)
                .Where(value => value.EventId == eventId && value.TeamTable.TeamId == scheduledEvent.TeamId.Value)
                .OrderByDescending(value => value.CreatedAt)
                .ToListAsync(cancellationToken);

            var teamJerseyNumbers = await GetTeamJerseyNumbersAsync(scheduledEvent.TeamId.Value, cancellationToken);
            return protocols.Select(value => ToProtocolDto(value, canManage, teamJerseyNumbers)).ToList();
        }

        public async Task<EventTableProtocolDto> CreateEventProtocol(Guid eventId, Guid actorUserId, CreateEventTableProtocolRequest request, CancellationToken cancellationToken)
        {
            var scheduledEvent = await _context.Events.AsNoTracking().FirstOrDefaultAsync(value => value.Id == eventId, cancellationToken);
            if (scheduledEvent?.TeamId == null)
            {
                throw new NotFoundException("Командное мероприятие не найдено.");
            }

            if (!await CanManageTeamAsync(scheduledEvent.TeamId.Value, actorUserId, cancellationToken))
            {
                throw new UnauthorizedException("Недостаточно прав");
            }

            var table = await _context.TeamTables
                .FirstOrDefaultAsync(value => value.Id == request.TeamTableId && value.TeamId == scheduledEvent.TeamId.Value, cancellationToken);

            if (table == null)
            {
                throw new NotFoundException("Основная таблица не найдена.");
            }

            var existingProtocol = await _context.EventTableProtocols
                .AsNoTracking()
                .AnyAsync(value => value.EventId == eventId && value.TeamTable.TemplateType == table.TemplateType, cancellationToken);

            if (existingProtocol)
            {
                throw new ConflictException("Для этого мероприятия уже есть протокол выбранного шаблона.");
            }

            await using var transaction = await _context.Database.BeginTransactionAsync(cancellationToken);
            await SyncTableRowsAsync(table.Id, table.TeamId, cancellationToken);
            await _context.Entry(table).Collection(value => value.Rows).LoadAsync(cancellationToken);

            var attendedUserIds = await _context.Attendances
                .AsNoTracking()
                .Where(value =>
                    value.EventId == eventId &&
                    value.Status == AttendanceStatus.Confirmed)
                .Select(value => value.UserId)
                .ToListAsync(cancellationToken);
            var attendedUserIdSet = attendedUserIds.ToHashSet();

            var protocol = new EventTableProtocol
            {
                EventId = eventId,
                TeamTableId = table.Id,
                CreatedByUserId = actorUserId,
                CreatedAt = _timeProvider.GetUtcNow().UtcDateTime,
                UpdatedAt = _timeProvider.GetUtcNow().UtcDateTime
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
                    CreatedAt = _timeProvider.GetUtcNow().UtcDateTime,
                    UpdatedAt = _timeProvider.GetUtcNow().UtcDateTime
                });
            }

            await _context.EventTableProtocols.AddAsync(protocol, cancellationToken);
            await _context.SaveChangesAsync(cancellationToken);
            await RecalculateTableAsync(table.Id, cancellationToken);
            await transaction.CommitAsync(cancellationToken);

            var created = await _context.EventTableProtocols
                .AsNoTracking()
                .Include(value => value.Event)
                .Include(value => value.TeamTable)
                .Include(value => value.Rows)
                    .ThenInclude(value => value.User)
                .FirstAsync(value => value.Id == protocol.Id, cancellationToken);

            return ToProtocolDto(created, true, await GetTeamJerseyNumbersAsync(scheduledEvent.TeamId.Value, cancellationToken));
        }

        public async Task<EventTableProtocolDto> UpdateProtocol(
            Guid eventId,
            Guid protocolId,
            Guid actorUserId,
            UpdateEventTableProtocolRequest request, CancellationToken cancellationToken)
        {
            var protocol = await _context.EventTableProtocols
                .Include(value => value.Event)
                .Include(value => value.TeamTable)
                .Include(value => value.Rows)
                    .ThenInclude(value => value.User)
                .FirstOrDefaultAsync(value => value.Id == protocolId && value.EventId == eventId, cancellationToken);

            if (protocol?.Event.TeamId == null || protocol.TeamTable.TeamId != protocol.Event.TeamId)
            {
                throw new NotFoundException("Протокол не найден.");
            }

            if (!await CanManageTeamAsync(protocol.Event.TeamId.Value, actorUserId, cancellationToken))
            {
                throw new UnauthorizedException("Недостаточно прав");
            }

            if (request.Rows.Select(value => value.RowId).Distinct().Count() != request.Rows.Count)
                throw new BusinessRuleException("Строки протокола не должны повторяться.");
            if (request.Rows.Any(value => protocol.Rows.All(row => row.Id != value.RowId)))
                throw new NotFoundException("Строка протокола не найдена.");

            await using var transaction = await _context.Database.BeginTransactionAsync(cancellationToken);
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
                row.UpdatedAt = _timeProvider.GetUtcNow().UtcDateTime;
            }

            protocol.UpdatedAt = _timeProvider.GetUtcNow().UtcDateTime;

            await _context.SaveChangesAsync(cancellationToken);
            await RecalculateTableAsync(protocol.TeamTableId, cancellationToken);
            await transaction.CommitAsync(cancellationToken);

            return ToProtocolDto(protocol, true, await GetTeamJerseyNumbersAsync(protocol.Event.TeamId.Value, cancellationToken));
        }

        public async Task<EventTableProtocolDto> UpdateProtocolRow(
            Guid eventId,
            Guid protocolId,
            Guid rowId,
            Guid actorUserId,
            UpdateEventTableProtocolRowRequest request, CancellationToken cancellationToken)
        {
            var protocol = await _context.EventTableProtocols
                .Include(value => value.Event)
                .Include(value => value.TeamTable)
                .Include(value => value.Rows)
                    .ThenInclude(value => value.User)
                .FirstOrDefaultAsync(value => value.Id == protocolId && value.EventId == eventId, cancellationToken);

            if (protocol?.Event.TeamId == null || protocol.TeamTable.TeamId != protocol.Event.TeamId)
            {
                throw new NotFoundException("Протокол не найден.");
            }

            if (!await CanManageTeamAsync(protocol.Event.TeamId.Value, actorUserId, cancellationToken))
            {
                throw new UnauthorizedException("Недостаточно прав");
            }

            var row = protocol.Rows.FirstOrDefault(value => value.Id == rowId);
            if (row == null)
            {
                throw new NotFoundException("Строка протокола не найдена.");
            }

            await using var transaction = await _context.Database.BeginTransactionAsync(cancellationToken);
            row.Games = ClampStat(request.Games);
            row.Goals = ClampStat(request.Goals);
            row.Assists = ClampStat(request.Assists);
            row.Points = row.Goals + row.Assists;
            row.UpdatedAt = _timeProvider.GetUtcNow().UtcDateTime;
            protocol.UpdatedAt = _timeProvider.GetUtcNow().UtcDateTime;

            await _context.SaveChangesAsync(cancellationToken);
            await RecalculateTableAsync(protocol.TeamTableId, cancellationToken);
            await transaction.CommitAsync(cancellationToken);

            return ToProtocolDto(protocol, true, await GetTeamJerseyNumbersAsync(protocol.Event.TeamId.Value, cancellationToken));
        }

        private async Task SyncTableRowsAsync(Guid tableId, Guid teamId, CancellationToken cancellationToken)
        {
            var existingUserIds = await _context.TeamTableRows
                .Where(value => value.TeamTableId == tableId)
                .Select(value => value.UserId)
                .ToListAsync(cancellationToken);

            var missingUserIds = await _context.TeamMemberships
                .AsNoTracking()
                .Where(value => value.TeamId == teamId && !existingUserIds.Contains(value.UserId))
                .Select(value => value.UserId)
                .ToListAsync(cancellationToken);

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
                    CreatedAt = _timeProvider.GetUtcNow().UtcDateTime,
                    UpdatedAt = _timeProvider.GetUtcNow().UtcDateTime
                }, cancellationToken);
            }

            await _context.SaveChangesAsync(cancellationToken);
        }

        private async Task RecalculateTableAsync(Guid tableId, CancellationToken cancellationToken)
        {
            var rows = await _context.TeamTableRows
                .Where(value => value.TeamTableId == tableId)
                .ToListAsync(cancellationToken);

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
                .ToDictionaryAsync(value => value.UserId, cancellationToken);

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

                row.UpdatedAt = _timeProvider.GetUtcNow().UtcDateTime;
            }

            await _context.SaveChangesAsync(cancellationToken);
        }

        private async Task<TeamMemberRole?> GetTeamRoleAsync(Guid teamId, Guid actorUserId, CancellationToken cancellationToken)
        {
            if (!await _context.Teams.AsNoTracking().AnyAsync(value => value.Id == teamId, cancellationToken))
                throw new NotFoundException("Команда не найдена.");
            return await _context.TeamMemberships.AsNoTracking()
                .Where(value => value.TeamId == teamId && value.UserId == actorUserId)
                .Select(value => (TeamMemberRole?)value.Role).SingleOrDefaultAsync(cancellationToken);
        }

        private async Task<bool> CanManageTeamAsync(Guid teamId, Guid actorUserId, CancellationToken cancellationToken) =>
            CoreTeamRolePolicy.CanManage(await GetTeamRoleAsync(teamId, actorUserId, cancellationToken));

        private static TeamTableDto ToTableDto(TeamTable table, bool canManage, IReadOnlyDictionary<Guid, int> teamJerseyNumbers)
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
                    .Select(value => ToTableRowDto(value, teamJerseyNumbers))
                    .ToList()
            };
        }

        private static TeamTableRowDto ToTableRowDto(TeamTableRow row, IReadOnlyDictionary<Guid, int> teamJerseyNumbers)
        {
            return new TeamTableRowDto
            {
                Id = row.Id,
                UserId = row.UserId,
                PlayerName = $"{row.User.LastName} {row.User.FirstName}".Trim(),
                JerseyNumber = teamJerseyNumbers.TryGetValue(row.UserId, out var teamNumber) ? teamNumber : row.User.JerseyNumber,
                PhotoUrl = row.User.PhotoUrl,
                Games = row.Games,
                Goals = row.Goals,
                Assists = row.Assists,
                Points = row.Points
            };
        }

        private static EventTableProtocolDto ToProtocolDto(EventTableProtocol protocol, bool canManage, IReadOnlyDictionary<Guid, int> teamJerseyNumbers)
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
                        JerseyNumber = teamJerseyNumbers.TryGetValue(value.UserId, out var teamNumber) ? teamNumber : value.User.JerseyNumber,
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

        private async Task<Dictionary<Guid, int>> GetTeamJerseyNumbersAsync(Guid teamId, CancellationToken cancellationToken)
        {
            return await _context.TeamMemberships
                .AsNoTracking()
                .Where(value => value.TeamId == teamId && value.TeamJerseyNumber.HasValue)
                .ToDictionaryAsync(value => value.UserId, value => value.TeamJerseyNumber!.Value, cancellationToken);
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
