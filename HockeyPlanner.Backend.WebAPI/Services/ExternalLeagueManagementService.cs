using HockeyPlanner.Backend.Core.Entities;
using HockeyPlanner.Backend.Core.Enums;
using HockeyPlanner.Backend.Core.Exceptions;
using HockeyPlanner.Backend.Infrastructure.Data;
using HockeyPlanner.Backend.WebAPI.Models.ExternalLeagues;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace HockeyPlanner.Backend.WebAPI.Services
{
    public sealed class ExternalLeagueManagementService(
        AppDbContext context,
        IExternalLeagueProviderResolver providerResolver,
        IExternalLeagueSyncService syncService) : IExternalLeagueManagementService
    {
        public async Task<IReadOnlyCollection<ExternalTeamSearchItem>> SearchTeamsAsync(
            ExternalLeagueProvider provider,
            string title,
            CancellationToken cancellationToken)
        {
            return await providerResolver.Resolve(provider)
                .SearchTeamsAsync(NormalizeTitle(title), cancellationToken);
        }

        public async Task<IReadOnlyCollection<ExternalLeagueLinkDto>> GetLinksAsync(
            Guid teamId,
            Guid actorUserId,
            CancellationToken cancellationToken)
        {
            await RequireManagementAccessAsync(teamId, actorUserId, cancellationToken);
            var links = await context.TeamExternalLeagueLinks.AsNoTracking()
                .Where(value => value.TeamId == teamId)
                .OrderBy(value => value.Provider)
                .ThenByDescending(value => value.IsPrimary)
                .ThenBy(value => value.CreatedAt)
                .ToArrayAsync(cancellationToken);
            return links.Select(Map).ToArray();
        }

        public async Task<ExternalLeagueLinkDto> CreateLinkAsync(
            Guid teamId,
            Guid actorUserId,
            CreateExternalLeagueLinkRequest request,
            CancellationToken cancellationToken)
        {
            await RequireManagementAccessAsync(teamId, actorUserId, cancellationToken);
            var externalTeamId = request.ExternalTeamId?.Trim() ?? string.Empty;
            if (externalTeamId.Length is < 1 or > 200)
            {
                throw new BusinessRuleException("Некорректный идентификатор внешней команды.");
            }

            var profile = await providerResolver.Resolve(request.Provider)
                .GetTeamProfileAsync(externalTeamId, cancellationToken)
                ?? throw new BusinessRuleException("Команда внешней лиги не найдена.");
            if (!string.Equals(profile.ExternalTeamId, externalTeamId, StringComparison.OrdinalIgnoreCase))
            {
                throw new BusinessRuleException("Провайдер вернул другой профиль команды.");
            }

            context.ChangeTracker.Clear();
            await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);
            var team = await context.Teams
                .FromSqlInterpolated($"SELECT * FROM teams WHERE id = {teamId} FOR UPDATE")
                .SingleOrDefaultAsync(cancellationToken)
                ?? throw new NotFoundException(nameof(Team), teamId);
            await RequireManagementAccessAsync(teamId, actorUserId, cancellationToken);

            var sameIdentity = await context.TeamExternalLeagueLinks
                .SingleOrDefaultAsync(value =>
                    value.Provider == request.Provider &&
                    value.ExternalTeamId == profile.ExternalTeamId,
                    cancellationToken);
            if (sameIdentity is not null && sameIdentity.TeamId != teamId)
            {
                throw new BusinessRuleException("Этот профиль внешней лиги уже привязан к другой команде.");
            }

            var providerLinks = await context.TeamExternalLeagueLinks
                .Where(value => value.TeamId == teamId && value.Provider == request.Provider)
                .OrderBy(value => value.CreatedAt)
                .ToListAsync(cancellationToken);
            var makePrimary = request.IsPrimary || providerLinks.Count == 0;
            var link = sameIdentity;
            if (link is null)
            {
                link = new TeamExternalLeagueLink
                {
                    TeamId = teamId,
                    Provider = request.Provider,
                    ExternalTeamId = profile.ExternalTeamId,
                    CreatedAt = DateTime.UtcNow
                };
                context.TeamExternalLeagueLinks.Add(link);
                providerLinks.Add(link);
            }

            ApplyProfile(link, profile);
            if (makePrimary)
            {
                foreach (var candidate in providerLinks)
                {
                    candidate.IsPrimary = candidate.Id == link.Id;
                }
            }

            if (request.Provider == ExternalLeagueProvider.Spbhl)
            {
                MirrorLegacyPrimary(team, providerLinks);
            }
            try
            {
                await context.SaveChangesAsync(cancellationToken);
                await transaction.CommitAsync(cancellationToken);
            }
            catch (DbUpdateException exception) when (IsIdentityConflict(exception))
            {
                throw new BusinessRuleException("Этот профиль внешней лиги уже привязан к другой команде.");
            }

            return Map(link);
        }

        public async Task DeleteLinkAsync(
            Guid teamId,
            Guid linkId,
            Guid actorUserId,
            CancellationToken cancellationToken)
        {
            await RequireManagementAccessAsync(teamId, actorUserId, cancellationToken);
            context.ChangeTracker.Clear();
            await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);
            var team = await context.Teams
                .FromSqlInterpolated($"SELECT * FROM teams WHERE id = {teamId} FOR UPDATE")
                .SingleOrDefaultAsync(cancellationToken)
                ?? throw new NotFoundException(nameof(Team), teamId);
            await RequireManagementAccessAsync(teamId, actorUserId, cancellationToken);
            var link = await context.TeamExternalLeagueLinks
                .SingleOrDefaultAsync(value => value.Id == linkId && value.TeamId == teamId, cancellationToken)
                ?? throw new NotFoundException(nameof(TeamExternalLeagueLink), linkId);
            var provider = link.Provider;
            var wasPrimary = link.IsPrimary;
            context.TeamExternalLeagueLinks.Remove(link);

            var remaining = await context.TeamExternalLeagueLinks
                .Where(value => value.TeamId == teamId && value.Provider == provider && value.Id != linkId)
                .OrderBy(value => value.CreatedAt)
                .ThenBy(value => value.Id)
                .ToListAsync(cancellationToken);
            if (wasPrimary && remaining.Count > 0)
            {
                remaining[0].IsPrimary = true;
            }
            if (provider == ExternalLeagueProvider.Spbhl)
            {
                MirrorLegacyPrimary(team, remaining);
            }
            await context.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }

        public async Task<AppliedTeamProfileDto> ApplyProfileAsync(
            Guid teamId,
            Guid linkId,
            Guid actorUserId,
            ApplyExternalLeagueProfileRequest request,
            CancellationToken cancellationToken)
        {
            await RequireManagementAccessAsync(teamId, actorUserId, cancellationToken);
            var team = await context.Teams.SingleOrDefaultAsync(value => value.Id == teamId, cancellationToken)
                ?? throw new NotFoundException(nameof(Team), teamId);
            var link = await context.TeamExternalLeagueLinks.AsNoTracking()
                .SingleOrDefaultAsync(value => value.Id == linkId && value.TeamId == teamId, cancellationToken)
                ?? throw new NotFoundException(nameof(TeamExternalLeagueLink), linkId);

            if (request.UseName && !string.IsNullOrWhiteSpace(link.ExternalTeamName))
            {
                team.Name = link.ExternalTeamName.Trim();
            }
            if (request.UseLogo && !string.IsNullOrWhiteSpace(link.LogoUrl))
            {
                team.AvatarUrl = link.LogoUrl.Trim();
            }
            if (request.UseCover && !string.IsNullOrWhiteSpace(link.CoverUrl))
            {
                team.CoverImageUrl = link.CoverUrl.Trim();
            }

            team.UpdatedAt = DateTime.UtcNow;
            await context.SaveChangesAsync(cancellationToken);
            return new AppliedTeamProfileDto
            {
                TeamId = team.Id,
                Name = team.Name,
                AvatarUrl = team.AvatarUrl,
                CoverImageUrl = team.CoverImageUrl
            };
        }

        public async Task<ExternalLeagueSyncResult> SyncLinkAsync(
            Guid teamId,
            Guid linkId,
            Guid actorUserId,
            CancellationToken cancellationToken)
        {
            await RequireManagementAccessAsync(teamId, actorUserId, cancellationToken);
            if (!await context.TeamExternalLeagueLinks.AsNoTracking()
                    .AnyAsync(value => value.Id == linkId && value.TeamId == teamId, cancellationToken))
            {
                throw new NotFoundException(nameof(TeamExternalLeagueLink), linkId);
            }
            return await syncService.SyncExternalLinkAsync(linkId, cancellationToken);
        }

        public async Task<IReadOnlyCollection<ExternalLeagueSyncResult>> SyncTeamAsync(
            Guid teamId,
            Guid actorUserId,
            CancellationToken cancellationToken)
        {
            await RequireManagementAccessAsync(teamId, actorUserId, cancellationToken);
            return await syncService.SyncTeamExternalLinksAsync(teamId, null, cancellationToken);
        }

        private async Task RequireManagementAccessAsync(Guid teamId, Guid actorUserId, CancellationToken cancellationToken)
        {
            if (!await context.Teams.AsNoTracking().AnyAsync(value => value.Id == teamId, cancellationToken))
            {
                throw new NotFoundException(nameof(Team), teamId);
            }
            var role = await context.TeamMemberships.AsNoTracking()
                .Where(value => value.TeamId == teamId && value.UserId == actorUserId)
                .Select(value => (TeamMemberRole?)value.Role)
                .SingleOrDefaultAsync(cancellationToken);
            if (role is not TeamMemberRole.Owner and not TeamMemberRole.Admin)
            {
                throw new UnauthorizedException("Недостаточно прав для управления внешними лигами команды.");
            }
        }

        private static void ApplyProfile(TeamExternalLeagueLink link, ExternalTeamProfile profile)
        {
            link.ExternalTeamName = profile.Name.Trim();
            link.DivisionName = NullIfEmpty(profile.DivisionName);
            link.ProfileUrl = NullIfEmpty(profile.ProfileUrl);
            link.LogoUrl = NullIfEmpty(profile.LogoUrl);
            link.CoverUrl = NullIfEmpty(profile.CoverUrl);
            link.City = NullIfEmpty(profile.City);
            link.Country = NullIfEmpty(profile.Country);
            link.UpdatedAt = DateTime.UtcNow;
        }

        private static void MirrorLegacyPrimary(Team team, IReadOnlyCollection<TeamExternalLeagueLink> links)
        {
            var primary = links.FirstOrDefault(value => value.Provider == ExternalLeagueProvider.Spbhl && value.IsPrimary);
            if (primary is null)
            {
                team.SpbhlTeamId = null;
                team.SpbhlTeamName = null;
                team.SpbhlLastSyncAttemptAt = null;
                team.SpbhlLastSuccessfulSyncAt = null;
                return;
            }

            team.SpbhlTeamId = Guid.TryParse(primary.ExternalTeamId, out var id) ? id : null;
            team.SpbhlTeamName = primary.ExternalTeamName;
            team.SpbhlLastSyncAttemptAt = primary.LastSyncAttemptAt;
            team.SpbhlLastSuccessfulSyncAt = primary.LastSuccessfulSyncAt;
        }

        private static ExternalLeagueLinkDto Map(TeamExternalLeagueLink link) => new()
        {
            Id = link.Id,
            TeamId = link.TeamId,
            Provider = link.Provider,
            ExternalTeamId = link.ExternalTeamId,
            ExternalTeamName = link.ExternalTeamName,
            DivisionName = link.DivisionName,
            ProfileUrl = link.ProfileUrl,
            LogoUrl = link.LogoUrl,
            CoverUrl = link.CoverUrl,
            City = link.City,
            Country = link.Country,
            IsPrimary = link.IsPrimary,
            LastSyncAttemptAt = link.LastSyncAttemptAt,
            LastSuccessfulSyncAt = link.LastSuccessfulSyncAt
        };

        private static string NormalizeTitle(string? title)
        {
            var normalized = title?.Trim() ?? string.Empty;
            if (normalized.Length is < 2 or > 100)
            {
                throw new BusinessRuleException("Название команды должно содержать от 2 до 100 символов.");
            }
            return normalized;
        }

        private static string? NullIfEmpty(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
        private static bool IsIdentityConflict(DbUpdateException exception) =>
            exception.InnerException is PostgresException postgres &&
            postgres.SqlState == PostgresErrorCodes.UniqueViolation;
    }
}
