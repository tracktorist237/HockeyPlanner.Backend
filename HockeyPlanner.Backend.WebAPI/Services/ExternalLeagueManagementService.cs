using HockeyPlanner.Backend.Core.Entities;
using HockeyPlanner.Backend.Core.Enums;
using HockeyPlanner.Backend.Core.Exceptions;
using HockeyPlanner.Backend.Infrastructure.Data;
using HockeyPlanner.Backend.WebAPI.Models.ExternalLeagues;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using HockeyPlanner.Backend.WebAPI.Models.Teams;

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
                    value.TeamId == teamId && value.Provider == request.Provider &&
                    value.ExternalTeamId == profile.ExternalTeamId,
                    cancellationToken);

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
                throw new BusinessRuleException("Этот профиль внешней лиги уже добавлен в команду.");
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

            var phoneCandidates = BuildValueCandidateMap(
                "phone",
                ExternalContactCandidateStorage.Deserialize(link.PhonesJson, PhoneFallbackLabel(link)),
                NormalizePhoneKey);
            var websiteCandidates = BuildValueCandidateMap(
                "website",
                ExternalContactCandidateStorage.Deserialize(link.WebsiteUrlsJson, "Сайт команды"),
                NormalizeWebsiteKey);
            var addressCandidates = await LoadAddressCandidatesAsync(teamId, cancellationToken);
            var selectedPhones = ResolveSelections(request.SelectedPhoneCandidateIds, phoneCandidates);
            var selectedWebsites = ResolveSelections(request.SelectedWebsiteCandidateIds, websiteCandidates);
            var addressMap = addressCandidates.ToDictionary(value => value.CandidateId, StringComparer.Ordinal);
            var selectedAddressIds = request.SelectedAddressCandidateIds ?? Array.Empty<string>();
            if (selectedAddressIds.Any(id => !addressMap.ContainsKey(id)))
            {
                throw new BusinessRuleException("Выбранные данные внешнего профиля больше недоступны.");
            }
            var selectedAddresses = selectedAddressIds.Distinct(StringComparer.Ordinal).Select(id => addressMap[id]).ToArray();

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
            if (request.UseDescriptionMetadata)
            {
                team.Description = MergeDescriptionMetadata(team.Description, link);
            }

            team.PhoneContactsJson = MergeContacts(team.PhoneContactsJson, selectedPhones, NormalizePhoneKey);
            team.LinkContactsJson = MergeContacts(team.LinkContactsJson, selectedWebsites, NormalizeWebsiteKey);
            team.AddressContactsJson = MergeAddressContacts(team.AddressContactsJson, selectedAddresses);

            team.UpdatedAt = DateTime.UtcNow;
            await context.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return new AppliedTeamProfileDto
            {
                TeamId = team.Id,
                Name = team.Name,
                AvatarUrl = team.AvatarUrl,
                CoverImageUrl = team.CoverImageUrl
            };
        }

        public async Task<IReadOnlyCollection<ExternalAddressCandidateDto>> GetAddressCandidatesAsync(
            Guid teamId,
            Guid actorUserId,
            CancellationToken cancellationToken)
        {
            await RequireManagementAccessAsync(teamId, actorUserId, cancellationToken);
            return await LoadAddressCandidatesAsync(teamId, cancellationToken);
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
            link.DivisionName = FirstNonEmpty(profile.DivisionName, link.DivisionName);
            link.ProfileUrl = FirstNonEmpty(profile.ProfileUrl, link.ProfileUrl);
            link.LogoUrl = FirstNonEmpty(profile.LogoUrl, link.LogoUrl);
            link.CoverUrl = FirstNonEmpty(profile.CoverUrl, link.CoverUrl);
            link.City = FirstNonEmpty(profile.City, link.City);
            link.Country = FirstNonEmpty(profile.Country, link.Country);
            link.FoundedYear = profile.FoundedYear ?? link.FoundedYear;
            link.CoachName = FirstNonEmpty(profile.CoachName, link.CoachName);
            link.AdministratorName = FirstNonEmpty(profile.AdministratorName, link.AdministratorName);
            link.PhonesJson = ExternalContactCandidateStorage.Merge(
                link.PhonesJson,
                profile.Phones,
                "Официальный контакт",
                NormalizePhoneKey);
            link.WebsiteUrlsJson = ExternalContactCandidateStorage.Merge(
                link.WebsiteUrlsJson,
                profile.WebsiteUrls,
                "Сайт команды",
                NormalizeWebsiteKey);
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
            FoundedYear = link.FoundedYear,
            CoachName = link.CoachName,
            AdministratorName = link.AdministratorName,
            PhoneCandidates = BuildCandidates(
                "phone",
                ExternalContactCandidateStorage.Deserialize(link.PhonesJson, PhoneFallbackLabel(link)),
                NormalizePhoneKey),
            WebsiteCandidates = BuildCandidates(
                "website",
                ExternalContactCandidateStorage.Deserialize(link.WebsiteUrlsJson, "Сайт команды"),
                NormalizeWebsiteKey),
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
        private static string? FirstNonEmpty(string? incoming, string? current) => NullIfEmpty(incoming) ?? NullIfEmpty(current);

        private async Task<IReadOnlyCollection<ExternalAddressCandidateDto>> LoadAddressCandidatesAsync(
            Guid teamId,
            CancellationToken cancellationToken)
        {
            var venues = await context.Events.AsNoTracking()
                .Where(value => value.TeamId == teamId &&
                                value.ExternalLeagueProvider != null &&
                                value.LocationAddress != "")
                .Select(value => new { value.LocationName, value.LocationAddress })
                .ToArrayAsync(cancellationToken);
            return venues
                .Select(value => new
                {
                    Venue = NormalizeWhitespace(value.LocationName),
                    Address = NormalizeWhitespace(value.LocationAddress)
                })
                .Where(value => !string.IsNullOrWhiteSpace(value.Address))
                .GroupBy(value => $"{value.Venue.ToUpperInvariant()}|{value.Address.ToUpperInvariant()}")
                .Select(group =>
                {
                    var display = group
                        .OrderByDescending(value => StartsWithUppercase(value.Venue))
                        .ThenBy(value => value.Venue, StringComparer.Ordinal)
                        .First();
                    return new ExternalAddressCandidateDto
                    {
                        CandidateId = CandidateId("address", group.Key),
                        VenueName = display.Venue,
                        Address = display.Address,
                        MatchCount = group.Count()
                    };
                })
                .OrderByDescending(value => value.MatchCount)
                .ThenBy(value => value.VenueName, StringComparer.OrdinalIgnoreCase)
                .ThenBy(value => value.Address, StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }

        private static IReadOnlyCollection<ExternalProfileCandidateDto> BuildCandidates(
            string kind,
            IEnumerable<ExternalContactCandidate> values,
            Func<string, string> normalizeKey) =>
            BuildValueCandidateMap(kind, values, normalizeKey)
                .Select(value => new ExternalProfileCandidateDto
                {
                    CandidateId = value.Key,
                    Value = value.Value.Value,
                    Label = value.Value.Label
                })
                .ToArray();

        private static Dictionary<string, ExternalContactCandidate> BuildValueCandidateMap(
            string kind,
            IEnumerable<ExternalContactCandidate> values,
            Func<string, string> normalizeKey) =>
            values.Where(value => !string.IsNullOrWhiteSpace(value.Value))
                .GroupBy(value => normalizeKey(value.Value), StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => CandidateId(kind, group.Key), group => group.First(), StringComparer.Ordinal);

        private static IReadOnlyCollection<ExternalContactCandidate> ResolveSelections(
            IReadOnlyCollection<string>? selectedIds,
            IReadOnlyDictionary<string, ExternalContactCandidate> candidates)
        {
            var ids = (selectedIds ?? Array.Empty<string>()).Distinct(StringComparer.Ordinal).ToArray();
            if (ids.Any(id => !candidates.ContainsKey(id)))
            {
                throw new BusinessRuleException("Выбранные данные внешнего профиля больше недоступны.");
            }
            return ids.Select(id => candidates[id]).ToArray();
        }

        private static string? MergeContacts(
            string? json,
            IEnumerable<ExternalContactCandidate> additions,
            Func<string, string> normalizeKey)
        {
            var contacts = DeserializeContacts(json).ToList();
            var existing = contacts.Select(value => normalizeKey(value.Value)).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var candidate in additions.Where(value => existing.Add(normalizeKey(value.Value))))
            {
                contacts.Add(new TeamContactItemDto
                {
                    Title = string.IsNullOrWhiteSpace(candidate.Label) ? "Официальный контакт" : candidate.Label.Trim(),
                    Value = candidate.Value.Trim()
                });
            }
            return contacts.Count == 0 ? null : JsonSerializer.Serialize(contacts.Take(10));
        }

        private static IReadOnlyCollection<TeamContactItemDto> DeserializeContacts(string? json)
        {
            if (string.IsNullOrWhiteSpace(json)) return Array.Empty<TeamContactItemDto>();
            try { return JsonSerializer.Deserialize<List<TeamContactItemDto>>(json) ?? []; }
            catch (JsonException) { return Array.Empty<TeamContactItemDto>(); }
        }

        private static string? MergeAddressContacts(
            string? json,
            IEnumerable<ExternalAddressCandidateDto> additions)
        {
            var contacts = DeserializeContacts(json).ToList();
            var existing = contacts.Select(value => NormalizeAddressKey(value.Value)).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var candidate in additions.Where(value => existing.Add(NormalizeAddressKey(value.Address))))
            {
                contacts.Add(new TeamContactItemDto
                {
                    Title = string.IsNullOrWhiteSpace(candidate.VenueName) ? "Арена из матчей" : candidate.VenueName,
                    Value = candidate.Address
                });
            }
            return contacts.Count == 0 ? null : JsonSerializer.Serialize(contacts.Take(10));
        }

        private static string PhoneFallbackLabel(TeamExternalLeagueLink link) =>
            string.IsNullOrWhiteSpace(link.AdministratorName) ? "Официальный контакт" : "Администратор";

        private static string MergeDescriptionMetadata(string? description, TeamExternalLeagueLink link)
        {
            const string heading = "Официальный профиль:";
            var lines = new List<string>();
            if (link.FoundedYear.HasValue) lines.Add($"Год создания: {link.FoundedYear.Value}");
            if (!string.IsNullOrWhiteSpace(link.CoachName)) lines.Add($"Тренер: {link.CoachName.Trim()}");
            if (!string.IsNullOrWhiteSpace(link.AdministratorName)) lines.Add($"Администратор: {link.AdministratorName.Trim()}");
            if (lines.Count == 0) return description?.Trim() ?? string.Empty;

            var current = description?.Trim() ?? string.Empty;
            var headingIndex = current.LastIndexOf(heading, StringComparison.Ordinal);
            if (headingIndex >= 0)
            {
                current = current[..headingIndex].TrimEnd();
            }
            var result = string.IsNullOrEmpty(current)
                ? $"{heading}\n{string.Join('\n', lines)}"
                : $"{current}\n\n{heading}\n{string.Join('\n', lines)}";
            return result.Length <= 1000 ? result : current;
        }

        private static string CandidateId(string kind, string normalizedValue) =>
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{kind}:{normalizedValue}"))).ToLowerInvariant();
        private static string NormalizeWhitespace(string? value) =>
            string.Join(" ", (value ?? string.Empty).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        private static string NormalizePhoneKey(string value)
        {
            var digits = new string(value.Where(char.IsDigit).ToArray());
            return digits.Length == 11 && digits[0] == '8' ? $"7{digits[1..]}" : digits;
        }
        private static string NormalizeWebsiteKey(string value)
        {
            if (Uri.TryCreate(value.Trim(), UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https")
            {
                return $"{uri.Host}{uri.AbsolutePath.TrimEnd('/')}{uri.Query}".ToUpperInvariant();
            }
            return value.Trim().TrimEnd('/').ToUpperInvariant();
        }
        private static string NormalizeAddressKey(string value) => NormalizeWhitespace(value).ToUpperInvariant();
        private static bool StartsWithUppercase(string value)
        {
            var first = value.FirstOrDefault(char.IsLetter);
            return first != default && char.IsUpper(first);
        }
        private static bool IsIdentityConflict(DbUpdateException exception) =>
            exception.InnerException is PostgresException postgres &&
            postgres.SqlState == PostgresErrorCodes.UniqueViolation;
    }
}
