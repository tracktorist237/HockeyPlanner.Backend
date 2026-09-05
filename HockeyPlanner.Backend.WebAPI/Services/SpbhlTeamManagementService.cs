using HockeyPlanner.Backend.Core.Entities;
using HockeyPlanner.Backend.Core.Enums;
using HockeyPlanner.Backend.Core.Exceptions;
using HockeyPlanner.Backend.Infrastructure.Data;
using HockeyPlanner.Backend.WebAPI.Models.Spbhl;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace HockeyPlanner.Backend.WebAPI.Services
{
    public sealed class SpbhlTeamManagementService : ISpbhlTeamManagementService
    {
        private const string InitialSyncError = "Команда привязана, но не удалось загрузить расписание СПбХЛ.";
        private readonly AppDbContext _context;
        private readonly ISpbhlClient _spbhlClient;
        private readonly ISpbhlTeamSyncService _syncService;
        private readonly ILogger<SpbhlTeamManagementService> _logger;

        public SpbhlTeamManagementService(
            AppDbContext context,
            ISpbhlClient spbhlClient,
            ISpbhlTeamSyncService syncService,
            ILogger<SpbhlTeamManagementService> logger)
        {
            _context = context;
            _spbhlClient = spbhlClient;
            _syncService = syncService;
            _logger = logger;
        }

        public async Task<SpbhlTeamLinkStatusDto> GetStatusAsync(
            Guid teamId,
            Guid actorUserId,
            CancellationToken cancellationToken)
        {
            var team = await GetAuthorizedTeamAsync(teamId, actorUserId, cancellationToken);
            var primary = await GetPrimaryLinkAsync(teamId, cancellationToken);
            return BuildStatus(team, primary);
        }

        public async Task<IReadOnlyCollection<SpbhlTeamSearchItem>> SearchTeamsAsync(
            Guid teamId,
            Guid actorUserId,
            string title,
            CancellationToken cancellationToken)
        {
            await GetAuthorizedTeamAsync(teamId, actorUserId, cancellationToken);
            return await _spbhlClient.SearchTeamsAsync(NormalizeSearchTitle(title), cancellationToken);
        }

        public async Task<SpbhlTeamBindResult> BindAsync(
            Guid teamId,
            Guid actorUserId,
            BindSpbhlTeamRequest request,
            CancellationToken cancellationToken)
        {
            var team = await GetAuthorizedTeamAsync(teamId, actorUserId, cancellationToken);
            if (request.SpbhlTeamId == Guid.Empty)
            {
                throw new BusinessRuleException("Некорректный идентификатор команды СПбХЛ.");
            }

            var currentPrimary = await GetPrimaryLinkAsync(teamId, cancellationToken);
            var currentPrimaryId = currentPrimary is not null && Guid.TryParse(currentPrimary.ExternalTeamId, out var parsedPrimaryId)
                ? parsedPrimaryId
                : team.SpbhlTeamId;
            if (currentPrimaryId.HasValue && currentPrimaryId != request.SpbhlTeamId)
            {
                throw new BusinessRuleException(
                    "Команда уже привязана к другому профилю СПбХЛ. Сначала удалите текущую привязку.");
            }

            var requestedName = NormalizeSearchTitle(request.SpbhlTeamName);
            var searchResults = await _spbhlClient.SearchTeamsAsync(requestedName, cancellationToken);
            var authoritativeTeam = searchResults.FirstOrDefault(value => value.TeamId == request.SpbhlTeamId)
                ?? throw new BusinessRuleException("Команда СПбХЛ не найдена");

            var teamLinks = await _context.TeamExternalLeagueLinks
                .Where(value => value.TeamId == teamId && value.Provider == ExternalLeagueProvider.Spbhl)
                .ToListAsync(cancellationToken);
            var link = teamLinks.SingleOrDefault(value =>
                string.Equals(value.ExternalTeamId, authoritativeTeam.TeamId.ToString("D"), StringComparison.OrdinalIgnoreCase));
            if (link is null)
            {
                link = new TeamExternalLeagueLink
                {
                    TeamId = teamId,
                    Provider = ExternalLeagueProvider.Spbhl,
                    ExternalTeamId = authoritativeTeam.TeamId.ToString("D"),
                    CreatedAt = DateTime.UtcNow
                };
                _context.TeamExternalLeagueLinks.Add(link);
                teamLinks.Add(link);
            }
            foreach (var candidate in teamLinks)
            {
                candidate.IsPrimary = candidate.Id == link.Id;
            }
            link.ExternalTeamName = authoritativeTeam.Name.Trim();
            link.DivisionName = authoritativeTeam.DivisionName;
            link.ProfileUrl = authoritativeTeam.ProfileUrl;
            link.LogoUrl = authoritativeTeam.LogoUrl;
            link.City = authoritativeTeam.City;
            link.Country = authoritativeTeam.Country;
            link.UpdatedAt = DateTime.UtcNow;

            team.SpbhlTeamId = authoritativeTeam.TeamId;
            team.SpbhlTeamName = authoritativeTeam.Name.Trim();
            try
            {
                await _context.SaveChangesAsync(cancellationToken);
            }
            catch (DbUpdateException exception) when (IsSpbhlTeamIdentityConflict(exception))
            {
                throw new BusinessRuleException("Этот профиль СПбХЛ уже добавлен в команду.");
            }

            try
            {
                var sync = await _syncService.SyncTeamAsync(teamId, cancellationToken);
                return new SpbhlTeamBindResult
                {
                    Link = BuildStatus(team),
                    InitialSyncSucceeded = true,
                    Sync = sync
                };
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                _logger.LogWarning(
                    "SPbHL initial sync timed out for TeamId {TeamId}, SpbhlTeamId {SpbhlTeamId}",
                    teamId,
                    team.SpbhlTeamId);
                return BuildFailedInitialSyncResult(team);
            }
            catch (HttpRequestException exception)
            {
                _logger.LogWarning(
                    exception,
                    "SPbHL initial sync failed for TeamId {TeamId}, SpbhlTeamId {SpbhlTeamId}",
                    teamId,
                    team.SpbhlTeamId);
                return BuildFailedInitialSyncResult(team);
            }
        }

        public async Task<SpbhlTeamLinkStatusDto> UnbindAsync(
            Guid teamId,
            Guid actorUserId,
            CancellationToken cancellationToken)
        {
            var team = await GetAuthorizedTeamAsync(teamId, actorUserId, cancellationToken);
            var links = await _context.TeamExternalLeagueLinks
                .Where(value => value.TeamId == teamId && value.Provider == ExternalLeagueProvider.Spbhl)
                .ToListAsync(cancellationToken);
            _context.TeamExternalLeagueLinks.RemoveRange(links);
            team.SpbhlTeamId = null;
            team.SpbhlTeamName = null;
            team.SpbhlLastSyncAttemptAt = null;
            team.SpbhlLastSuccessfulSyncAt = null;
            await _context.SaveChangesAsync(cancellationToken);
            return BuildStatus(team);
        }

        public async Task<SpbhlTeamSyncResult> SyncNowAsync(
            Guid teamId,
            Guid actorUserId,
            CancellationToken cancellationToken)
        {
            var team = await GetAuthorizedTeamAsync(teamId, actorUserId, cancellationToken);
            if (!team.SpbhlTeamId.HasValue)
            {
                throw new BusinessRuleException("Команда не привязана к профилю СПбХЛ.");
            }

            return await _syncService.SyncTeamAsync(teamId, cancellationToken);
        }

        private async Task<Team> GetAuthorizedTeamAsync(
            Guid teamId,
            Guid actorUserId,
            CancellationToken cancellationToken)
        {
            var team = await _context.Teams.SingleOrDefaultAsync(value => value.Id == teamId, cancellationToken)
                ?? throw new NotFoundException(nameof(Team), teamId);
            var role = await _context.TeamMemberships.AsNoTracking()
                .Where(value => value.TeamId == teamId && value.UserId == actorUserId)
                .Select(value => (TeamMemberRole?)value.Role)
                .SingleOrDefaultAsync(cancellationToken);

            if (role is not TeamMemberRole.Owner and not TeamMemberRole.Admin)
            {
                throw new UnauthorizedException("Недостаточно прав для управления привязкой СПбХЛ.");
            }

            return team;
        }

        private static string NormalizeSearchTitle(string? title)
        {
            var normalized = title?.Trim() ?? string.Empty;
            if (normalized.Length is < 2 or > 100)
            {
                throw new BusinessRuleException("Название команды должно содержать от 2 до 100 символов.");
            }

            return normalized;
        }

        private async Task<TeamExternalLeagueLink?> GetPrimaryLinkAsync(Guid teamId, CancellationToken cancellationToken)
        {
            return await _context.TeamExternalLeagueLinks.AsNoTracking()
                .Where(value => value.TeamId == teamId && value.Provider == ExternalLeagueProvider.Spbhl)
                .OrderByDescending(value => value.IsPrimary)
                .ThenBy(value => value.CreatedAt)
                .FirstOrDefaultAsync(cancellationToken);
        }

        private static SpbhlTeamLinkStatusDto BuildStatus(Team team, TeamExternalLeagueLink? primary = null)
        {
            var linkedId = primary is not null && Guid.TryParse(primary.ExternalTeamId, out var parsedId)
                ? parsedId
                : team.SpbhlTeamId;
            return new SpbhlTeamLinkStatusDto
            {
                TeamId = team.Id,
                IsLinked = linkedId.HasValue,
                SpbhlTeamId = linkedId,
                SpbhlTeamName = primary?.ExternalTeamName ?? team.SpbhlTeamName,
                ProfileUrl = primary?.ProfileUrl ?? (linkedId.HasValue
                    ? $"https://spbhl.ru/Team?TeamID={linkedId.Value}"
                    : null),
                LastSyncAttemptAt = primary?.LastSyncAttemptAt ?? team.SpbhlLastSyncAttemptAt,
                LastSuccessfulSyncAt = primary?.LastSuccessfulSyncAt ?? team.SpbhlLastSuccessfulSyncAt
            };
        }

        private static SpbhlTeamBindResult BuildFailedInitialSyncResult(Team team)
        {
            return new SpbhlTeamBindResult
            {
                Link = BuildStatus(team),
                InitialSyncSucceeded = false,
                SyncError = InitialSyncError
            };
        }

        private static bool IsSpbhlTeamIdentityConflict(DbUpdateException exception)
        {
            return exception.InnerException is PostgresException postgresException &&
                postgresException.SqlState == PostgresErrorCodes.UniqueViolation &&
                postgresException.ConstraintName == "i_x_team_external_league_links_team_id_provider_external_team_id";
        }
    }
}
