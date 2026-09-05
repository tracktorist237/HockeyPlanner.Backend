using HockeyPlanner.Backend.Core.Entities;
using HockeyPlanner.Backend.Core.Enums;
using HockeyPlanner.Backend.Core.Exceptions;
using HockeyPlanner.Backend.Infrastructure.Data;
using HockeyPlanner.Backend.WebAPI.Models.ExternalLeagues;
using Microsoft.EntityFrameworkCore;

namespace HockeyPlanner.Backend.WebAPI.Services
{
    public sealed class ExternalLeagueSyncService(
        AppDbContext context,
        IExternalLeagueProviderResolver providerResolver,
        ILogger<ExternalLeagueSyncService> logger) : IExternalLeagueSyncService
    {
        public async Task<ExternalLeagueSyncResult> SyncExternalLinkAsync(
            Guid linkId,
            CancellationToken cancellationToken)
        {
            var linkSnapshot = await context.TeamExternalLeagueLinks.AsNoTracking()
                .SingleOrDefaultAsync(value => value.Id == linkId, cancellationToken)
                ?? throw new NotFoundException(nameof(TeamExternalLeagueLink), linkId);
            var expectedProvider = linkSnapshot.Provider;
            var expectedExternalTeamId = linkSnapshot.ExternalTeamId;
            var attemptAt = DateTime.UtcNow;

            await context.TeamExternalLeagueLinks
                .Where(value => value.Id == linkId)
                .ExecuteUpdateAsync(
                    setters => setters.SetProperty(value => value.LastSyncAttemptAt, attemptAt),
                    cancellationToken);
            if (linkSnapshot.IsPrimary && linkSnapshot.Provider == ExternalLeagueProvider.Spbhl)
            {
                await context.Teams
                    .Where(value => value.Id == linkSnapshot.TeamId)
                    .ExecuteUpdateAsync(
                        setters => setters.SetProperty(value => value.SpbhlLastSyncAttemptAt, attemptAt),
                        cancellationToken);
            }

            var provider = providerResolver.Resolve(expectedProvider);
            ExternalTeamProfile? refreshedProfile;
            IReadOnlyCollection<ExternalMatch> receivedMatches;
            try
            {
                var profileTask = provider.GetTeamProfileAsync(expectedExternalTeamId, cancellationToken);
                var scheduleTask = provider.GetTeamScheduleAsync(expectedExternalTeamId, cancellationToken);
                await Task.WhenAll(profileTask, scheduleTask);
                refreshedProfile = await profileTask;
                receivedMatches = await scheduleTask;
            }
            catch (HttpRequestException exception)
            {
                logger.LogWarning(
                    exception,
                    "External league schedule request failed for TeamId {TeamId}, LinkId {LinkId}, Provider {Provider}, ExternalTeamId {ExternalTeamId}",
                    linkSnapshot.TeamId,
                    linkId,
                    expectedProvider,
                    expectedExternalTeamId);
                throw;
            }

            var matches = receivedMatches
                .Where(HasValidIdentity)
                .GroupBy(value => (value.ExternalCompetitionId, value.ExternalMatchId))
                .Select(group => group.First())
                .ToArray();
            var enrichmentRequestCount = await EnrichMatchesAsync(provider, matches, cancellationToken);
            var syncedAt = DateTime.UtcNow;
            var createdCount = 0;
            var updatedCount = 0;
            var unchangedCount = 0;
            var changes = new List<ExternalEventChange>();
            var createdEvents = new List<ExternalCreatedEvent>();

            context.ChangeTracker.Clear();
            await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);
            var currentTeam = await context.Teams
                .FromSqlInterpolated($"SELECT * FROM teams WHERE id = {linkSnapshot.TeamId} FOR UPDATE")
                .SingleOrDefaultAsync(cancellationToken)
                ?? throw new NotFoundException(nameof(Team), linkSnapshot.TeamId);
            var currentLink = await context.TeamExternalLeagueLinks
                .FromSqlInterpolated($"SELECT * FROM team_external_league_links WHERE id = {linkId} FOR UPDATE")
                .SingleOrDefaultAsync(cancellationToken);

            if (currentLink is null ||
                currentLink.TeamId != linkSnapshot.TeamId ||
                currentLink.Provider != expectedProvider ||
                !string.Equals(currentLink.ExternalTeamId, expectedExternalTeamId, StringComparison.Ordinal))
            {
                throw new BusinessRuleException("Привязка внешней лиги изменилась во время синхронизации.");
            }

            if (refreshedProfile is not null &&
                refreshedProfile.Provider == expectedProvider &&
                string.Equals(refreshedProfile.ExternalTeamId, expectedExternalTeamId, StringComparison.OrdinalIgnoreCase))
            {
                ApplyProfileMetadata(currentLink, refreshedProfile);
            }

            var existingEvents = await context.Events
                .Where(value =>
                    value.TeamId == linkSnapshot.TeamId &&
                    value.ExternalLeagueProvider == expectedProvider &&
                    value.ExternalCompetitionId != null &&
                    value.ExternalMatchId != null)
                .ToListAsync(cancellationToken);
            var existingByIdentity = existingEvents.ToDictionary(
                value => (value.ExternalCompetitionId!, value.ExternalMatchId!));
            var membershipUserIds = await context.TeamMemberships.AsNoTracking()
                .Where(value => value.TeamId == linkSnapshot.TeamId)
                .Select(value => value.UserId)
                .Distinct()
                .ToArrayAsync(cancellationToken);

            foreach (var match in matches)
            {
                var identity = (match.ExternalCompetitionId, match.ExternalMatchId);
                if (!existingByIdentity.TryGetValue(identity, out var scheduledEvent))
                {
                    scheduledEvent = CreateEvent(currentLink, match, syncedAt, membershipUserIds);
                    await context.Events.AddAsync(scheduledEvent, cancellationToken);
                    existingByIdentity[identity] = scheduledEvent;
                    createdCount++;
                    createdEvents.Add(new ExternalCreatedEvent
                    {
                        EventId = scheduledEvent.Id,
                        Title = scheduledEvent.Title
                    });
                    continue;
                }

                var previousStatus = scheduledEvent.Status;
                var changed = ApplySourceUpdate(scheduledEvent, currentLink, match);
                if (previousStatus != EventStatus.Rescheduled && scheduledEvent.Status == EventStatus.Rescheduled)
                {
                    changes.Add(new ExternalEventChange
                    {
                        EventId = scheduledEvent.Id,
                        Title = scheduledEvent.Title,
                        NewStartTime = scheduledEvent.StartTime,
                        PreviousStatus = previousStatus,
                        NewStatus = scheduledEvent.Status
                    });
                }
                scheduledEvent.ExternalLastSyncedAt = syncedAt;
                if (HasLegacyNumericIdentity(match))
                {
                    scheduledEvent.SpbhlLastSyncedAt = syncedAt;
                }
                if (changed)
                {
                    updatedCount++;
                }
                else
                {
                    unchangedCount++;
                }
            }

            currentLink.LastSuccessfulSyncAt = syncedAt;
            if (currentLink.IsPrimary && currentLink.Provider == ExternalLeagueProvider.Spbhl)
            {
                currentTeam.SpbhlLastSyncAttemptAt = attemptAt;
                currentTeam.SpbhlLastSuccessfulSyncAt = syncedAt;
            }

            await context.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);

            logger.LogInformation(
                "External league schedule synchronized: TeamId {TeamId}, LinkId {LinkId}, Provider {Provider}, ExternalTeamId {ExternalTeamId}, Received {Received}, Created {Created}, Updated {Updated}, Unchanged {Unchanged}, EnrichmentRequests {EnrichmentRequests}",
                linkSnapshot.TeamId,
                linkId,
                expectedProvider,
                expectedExternalTeamId,
                receivedMatches.Count,
                createdCount,
                updatedCount,
                unchangedCount,
                enrichmentRequestCount);

            return new ExternalLeagueSyncResult
            {
                TeamId = linkSnapshot.TeamId,
                LinkId = linkId,
                Provider = expectedProvider,
                ExternalTeamId = expectedExternalTeamId,
                ReceivedCount = receivedMatches.Count,
                CreatedCount = createdCount,
                UpdatedCount = updatedCount,
                UnchangedCount = unchangedCount,
                EnrichmentRequestCount = enrichmentRequestCount,
                SyncedAt = syncedAt,
                Changes = changes,
                CreatedEvents = createdEvents
            };
        }

        public async Task<IReadOnlyCollection<ExternalLeagueSyncResult>> SyncTeamExternalLinksAsync(
            Guid teamId,
            ExternalLeagueProvider? provider,
            CancellationToken cancellationToken)
        {
            if (!await context.Teams.AsNoTracking().AnyAsync(value => value.Id == teamId, cancellationToken))
            {
                throw new NotFoundException(nameof(Team), teamId);
            }

            var linkIds = await context.TeamExternalLeagueLinks.AsNoTracking()
                .Where(value => value.TeamId == teamId && (!provider.HasValue || value.Provider == provider.Value))
                .OrderByDescending(value => value.IsPrimary)
                .ThenBy(value => value.CreatedAt)
                .Select(value => value.Id)
                .ToArrayAsync(cancellationToken);
            if (linkIds.Length == 0)
            {
                throw new BusinessRuleException("Команда не привязана к внешней лиге.");
            }

            var results = new List<ExternalLeagueSyncResult>(linkIds.Length);
            foreach (var id in linkIds)
            {
                results.Add(await SyncExternalLinkAsync(id, cancellationToken));
            }

            return results;
        }

        private static async Task<int> EnrichMatchesAsync(
            IExternalLeagueProvider provider,
            IReadOnlyCollection<ExternalMatch> matches,
            CancellationToken cancellationToken)
        {
            var count = 0;
            foreach (var match in matches.Where(NeedsDetails))
            {
                var details = await provider.GetMatchDetailsAsync(
                    match.ExternalCompetitionId,
                    match.ExternalMatchId,
                    cancellationToken);
                count++;
                if (details is null)
                {
                    continue;
                }
                if (!string.Equals(details.ExternalCompetitionId, match.ExternalCompetitionId, StringComparison.Ordinal) ||
                    !string.Equals(details.ExternalMatchId, match.ExternalMatchId, StringComparison.Ordinal))
                {
                    continue;
                }

                match.HomeScore ??= details.HomeScore;
                match.AwayScore ??= details.AwayScore;
                match.ArenaName ??= details.ArenaName;
                match.ArenaAddress ??= details.ArenaAddress;
                match.TournamentName ??= details.TournamentName;
                match.DivisionName ??= details.DivisionName;
                if (details.Status != ExternalMatchStatus.Unknown)
                {
                    match.Status = details.Status;
                }
            }

            return count;
        }

        private static bool NeedsDetails(ExternalMatch match) =>
            (match.Status == ExternalMatchStatus.Finished &&
             (!match.HomeScore.HasValue || !match.AwayScore.HasValue)) ||
            (!string.IsNullOrWhiteSpace(match.ArenaName) && string.IsNullOrWhiteSpace(match.ArenaAddress));

        private static ScheduledEvent CreateEvent(
            TeamExternalLeagueLink link,
            ExternalMatch match,
            DateTime syncedAt,
            IReadOnlyCollection<Guid> membershipUserIds)
        {
            var homeTeamName = match.HomeTeamName.Trim();
            var awayTeamName = match.AwayTeamName.Trim();
            var scheduledEvent = new ScheduledEvent
            {
                Title = BuildTitle(homeTeamName, awayTeamName),
                Type = EventType.Game,
                StartTime = match.StartTime.UtcDateTime,
                DurationMinutes = 75,
                Status = MapInitialStatus(match.Status),
                LocationName = match.ArenaName?.Trim() ?? string.Empty,
                LocationAddress = match.ArenaAddress?.Trim() ?? string.Empty,
                HomeTeamName = homeTeamName,
                AwayTeamName = awayTeamName,
                TeamId = link.TeamId,
                ExternalLeagueProvider = link.Provider,
                ExternalCompetitionId = match.ExternalCompetitionId,
                ExternalMatchId = match.ExternalMatchId,
                ExternalMatchUrl = match.MatchUrl.Trim(),
                ExternalLastSyncedAt = syncedAt,
                ExternalDivisionName = FirstNonEmpty(match.DivisionName, link.DivisionName),
                ExternalTournamentName = NullIfEmpty(match.TournamentName),
                SpbhlTournamentId = match.LegacyNumericCompetitionId,
                SpbhlMatchId = match.LegacyNumericMatchId,
                SpbhlMatchUrl = HasLegacyNumericIdentity(match) ? match.MatchUrl.Trim() : null,
                SpbhlLastSyncedAt = HasLegacyNumericIdentity(match) ? syncedAt : null,
                HomeScore = match.HomeScore,
                AwayScore = match.AwayScore,
                CreatedAt = syncedAt
            };

            scheduledEvent.Attendances = membershipUserIds.Select(userId => new Attendance
            {
                EventId = scheduledEvent.Id,
                UserId = userId,
                Status = AttendanceStatus.Pending,
                CreatedAt = syncedAt,
                RespondedAt = syncedAt
            }).ToList();
            return scheduledEvent;
        }

        private static bool ApplySourceUpdate(
            ScheduledEvent scheduledEvent,
            TeamExternalLeagueLink link,
            ExternalMatch match)
        {
            var changed = false;
            var home = match.HomeTeamName.Trim();
            var away = match.AwayTeamName.Trim();
            changed |= SetIfDifferent(scheduledEvent.StartTime, match.StartTime.UtcDateTime, value => scheduledEvent.StartTime = value);
            changed |= SetIfDifferent(scheduledEvent.HomeTeamName, home, value => scheduledEvent.HomeTeamName = value);
            changed |= SetIfDifferent(scheduledEvent.AwayTeamName, away, value => scheduledEvent.AwayTeamName = value);
            changed |= SetIfDifferent(scheduledEvent.Title, BuildTitle(home, away), value => scheduledEvent.Title = value);
            changed |= SetIfDifferent(scheduledEvent.ExternalMatchUrl, match.MatchUrl.Trim(), value => scheduledEvent.ExternalMatchUrl = value);
            if (HasLegacyNumericIdentity(match))
            {
                changed |= SetIfDifferent(scheduledEvent.SpbhlMatchUrl, match.MatchUrl.Trim(), value => scheduledEvent.SpbhlMatchUrl = value);
            }

            changed |= SetNonEmpty(match.ArenaName, scheduledEvent.LocationName, value => scheduledEvent.LocationName = value);
            changed |= SetNonEmpty(match.ArenaAddress, scheduledEvent.LocationAddress, value => scheduledEvent.LocationAddress = value);
            if (!string.IsNullOrWhiteSpace(match.DivisionName))
            {
                changed |= SetNonEmpty(
                    match.DivisionName,
                    scheduledEvent.ExternalDivisionName,
                    value => scheduledEvent.ExternalDivisionName = value);
            }
            else if (string.IsNullOrWhiteSpace(scheduledEvent.ExternalDivisionName))
            {
                changed |= SetNonEmpty(
                    link.DivisionName,
                    scheduledEvent.ExternalDivisionName,
                    value => scheduledEvent.ExternalDivisionName = value);
            }
            changed |= SetNonEmpty(
                match.TournamentName,
                scheduledEvent.ExternalTournamentName,
                value => scheduledEvent.ExternalTournamentName = value);

            if (match.HomeScore.HasValue)
            {
                changed |= SetIfDifferent(scheduledEvent.HomeScore, match.HomeScore, value => scheduledEvent.HomeScore = value);
            }
            if (match.AwayScore.HasValue)
            {
                changed |= SetIfDifferent(scheduledEvent.AwayScore, match.AwayScore, value => scheduledEvent.AwayScore = value);
            }
            changed |= ApplyStatus(scheduledEvent, match.Status);

            return changed;
        }

        private static bool SetNonEmpty(string? incoming, string? current, Action<string> setter)
        {
            var normalized = NullIfEmpty(incoming);
            if (normalized is null || string.Equals(current, normalized, StringComparison.Ordinal))
            {
                return false;
            }
            setter(normalized);
            return true;
        }

        private static bool SetIfDifferent<T>(T current, T incoming, Action<T> setter)
        {
            if (EqualityComparer<T>.Default.Equals(current, incoming))
            {
                return false;
            }
            setter(incoming);
            return true;
        }

        private static string? FirstNonEmpty(string? first, string? second) => NullIfEmpty(first) ?? NullIfEmpty(second);
        private static EventStatus MapInitialStatus(ExternalMatchStatus status) => status switch
        {
            ExternalMatchStatus.Finished => EventStatus.Completed,
            ExternalMatchStatus.Rescheduled => EventStatus.Rescheduled,
            ExternalMatchStatus.Cancelled => EventStatus.Cancelled,
            _ => EventStatus.Scheduled
        };

        private static bool ApplyStatus(ScheduledEvent scheduledEvent, ExternalMatchStatus incoming)
        {
            EventStatus? target = incoming switch
            {
                ExternalMatchStatus.Finished => EventStatus.Completed,
                ExternalMatchStatus.Rescheduled when scheduledEvent.Status != EventStatus.Completed => EventStatus.Rescheduled,
                ExternalMatchStatus.Cancelled when scheduledEvent.Status != EventStatus.Completed => EventStatus.Cancelled,
                ExternalMatchStatus.Scheduled when scheduledEvent.Status is not EventStatus.Completed and not EventStatus.Cancelled => EventStatus.Scheduled,
                _ => null
            };
            return target.HasValue && SetIfDifferent(scheduledEvent.Status, target.Value, value => scheduledEvent.Status = value);
        }

        private static void ApplyProfileMetadata(TeamExternalLeagueLink link, ExternalTeamProfile profile)
        {
            link.ExternalTeamName = FirstNonEmpty(profile.Name, link.ExternalTeamName) ?? link.ExternalTeamName;
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
        private static bool HasValidIdentity(ExternalMatch match) =>
            !string.IsNullOrWhiteSpace(match.ExternalCompetitionId) &&
            !string.IsNullOrWhiteSpace(match.ExternalMatchId);
        private static bool HasLegacyNumericIdentity(ExternalMatch match) =>
            match.LegacyNumericCompetitionId.HasValue && match.LegacyNumericMatchId.HasValue;
        private static string? NullIfEmpty(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
        private static string BuildTitle(string home, string away) => $"{home} — {away}";
    }
}
