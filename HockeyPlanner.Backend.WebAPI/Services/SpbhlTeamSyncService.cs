using HockeyPlanner.Backend.Core.Entities;
using HockeyPlanner.Backend.Core.Enums;
using HockeyPlanner.Backend.Core.Exceptions;
using HockeyPlanner.Backend.Infrastructure.Data;
using HockeyPlanner.Backend.WebAPI.Models.Spbhl;
using Microsoft.EntityFrameworkCore;

namespace HockeyPlanner.Backend.WebAPI.Services
{
    public class SpbhlTeamSyncService : ISpbhlTeamSyncService
    {
        private readonly AppDbContext _context;
        private readonly ISpbhlClient _spbhlClient;
        private readonly ILogger<SpbhlTeamSyncService> _logger;

        public SpbhlTeamSyncService(
            AppDbContext context,
            ISpbhlClient spbhlClient,
            ILogger<SpbhlTeamSyncService> logger)
        {
            _context = context;
            _spbhlClient = spbhlClient;
            _logger = logger;
        }

        public async Task<SpbhlTeamSyncResult> SyncTeamAsync(
            Guid teamId,
            CancellationToken cancellationToken)
        {
            var team = await _context.Teams
                .SingleOrDefaultAsync(value => value.Id == teamId, cancellationToken)
                ?? throw new NotFoundException(nameof(Team), teamId);

            if (!team.SpbhlTeamId.HasValue)
            {
                throw new BusinessRuleException("Команда не привязана к профилю СПбХЛ.");
            }

            var expectedSpbhlTeamId = team.SpbhlTeamId.Value;
            team.SpbhlLastSyncAttemptAt = DateTime.UtcNow;
            await _context.SaveChangesAsync(cancellationToken);
            _context.Entry(team).State = EntityState.Detached;

            IReadOnlyCollection<SpbhlMatchItem> receivedMatches;
            try
            {
                receivedMatches = await _spbhlClient.GetTeamScheduleAsync(expectedSpbhlTeamId, cancellationToken);
            }
            catch (HttpRequestException exception)
            {
                _logger.LogWarning(
                    exception,
                    "SPbHL schedule request failed for TeamId {TeamId}, SpbhlTeamId {SpbhlTeamId}",
                    teamId,
                    expectedSpbhlTeamId);
                throw;
            }

            var matches = receivedMatches
                .GroupBy(value => (value.TournamentId, value.MatchId))
                .Select(group => group.First())
                .ToArray();
            var syncedAt = DateTime.UtcNow;
            var createdCount = 0;
            var updatedCount = 0;
            var unchangedCount = 0;

            await using var transaction = await _context.Database.BeginTransactionAsync(cancellationToken);

            var currentTeam = await _context.Teams
                .FromSqlInterpolated($"SELECT * FROM teams WHERE id = {teamId} FOR UPDATE")
                .SingleOrDefaultAsync(cancellationToken)
                ?? throw new NotFoundException(nameof(Team), teamId);
            if (currentTeam.SpbhlTeamId != expectedSpbhlTeamId)
            {
                throw new BusinessRuleException("Привязка команды СПбХЛ изменилась во время синхронизации.");
            }

            var existingEvents = await _context.Events
                .Where(value =>
                    value.TeamId == teamId &&
                    value.SpbhlTournamentId.HasValue &&
                    value.SpbhlMatchId.HasValue)
                .ToListAsync(cancellationToken);
            var existingByIdentity = existingEvents.ToDictionary(
                value => (value.SpbhlTournamentId!.Value, value.SpbhlMatchId!.Value));
            var membershipUserIds = await _context.TeamMemberships
                .AsNoTracking()
                .Where(value => value.TeamId == teamId)
                .Select(value => value.UserId)
                .Distinct()
                .ToArrayAsync(cancellationToken);

            foreach (var match in matches)
            {
                var identity = (match.TournamentId, match.MatchId);
                if (!existingByIdentity.TryGetValue(identity, out var scheduledEvent))
                {
                    scheduledEvent = CreateEvent(teamId, match, syncedAt, membershipUserIds);
                    await _context.Events.AddAsync(scheduledEvent, cancellationToken);
                    existingByIdentity[identity] = scheduledEvent;
                    createdCount++;
                    continue;
                }

                var changed = ApplySourceUpdate(scheduledEvent, match);
                scheduledEvent.SpbhlLastSyncedAt = syncedAt;
                if (changed)
                {
                    updatedCount++;
                }
                else
                {
                    unchangedCount++;
                }
            }

            currentTeam.SpbhlLastSuccessfulSyncAt = syncedAt;
            await _context.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);

            _logger.LogInformation(
                "SPbHL team schedule synchronized: TeamId {TeamId}, SpbhlTeamId {SpbhlTeamId}, Received {Received}, Created {Created}, Updated {Updated}, Unchanged {Unchanged}",
                teamId,
                expectedSpbhlTeamId,
                receivedMatches.Count,
                createdCount,
                updatedCount,
                unchangedCount);

            return new SpbhlTeamSyncResult
            {
                TeamId = teamId,
                SpbhlTeamId = expectedSpbhlTeamId,
                ReceivedCount = receivedMatches.Count,
                CreatedCount = createdCount,
                UpdatedCount = updatedCount,
                UnchangedCount = unchangedCount,
                SyncedAt = syncedAt
            };
        }

        private static ScheduledEvent CreateEvent(
            Guid teamId,
            SpbhlMatchItem match,
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
                Status = match.Status == SpbhlMatchStatus.Finished
                    ? EventStatus.Completed
                    : EventStatus.Scheduled,
                LocationName = match.ArenaName?.Trim() ?? string.Empty,
                LocationAddress = string.Empty,
                HomeTeamName = homeTeamName,
                AwayTeamName = awayTeamName,
                TeamId = teamId,
                SpbhlTournamentId = match.TournamentId,
                SpbhlMatchId = match.MatchId,
                SpbhlMatchUrl = match.MatchUrl.Trim(),
                SpbhlLastSyncedAt = syncedAt,
                HomeScore = match.HomeScore,
                AwayScore = match.AwayScore,
                CreatedAt = syncedAt
            };

            scheduledEvent.Attendances = membershipUserIds
                .Select(userId => new Attendance
                {
                    EventId = scheduledEvent.Id,
                    UserId = userId,
                    Status = AttendanceStatus.Pending,
                    CreatedAt = scheduledEvent.CreatedAt,
                    RespondedAt = scheduledEvent.CreatedAt
                })
                .ToList();

            return scheduledEvent;
        }

        private static bool ApplySourceUpdate(ScheduledEvent scheduledEvent, SpbhlMatchItem match)
        {
            var changed = false;
            var homeTeamName = match.HomeTeamName.Trim();
            var awayTeamName = match.AwayTeamName.Trim();
            var title = BuildTitle(homeTeamName, awayTeamName);
            var startTime = match.StartTime.UtcDateTime;
            var matchUrl = match.MatchUrl.Trim();

            changed |= SetIfDifferent(scheduledEvent.StartTime, startTime, value => scheduledEvent.StartTime = value);
            changed |= SetIfDifferent(scheduledEvent.HomeTeamName, homeTeamName, value => scheduledEvent.HomeTeamName = value);
            changed |= SetIfDifferent(scheduledEvent.AwayTeamName, awayTeamName, value => scheduledEvent.AwayTeamName = value);
            changed |= SetIfDifferent(scheduledEvent.Title, title, value => scheduledEvent.Title = value);
            changed |= SetIfDifferent(scheduledEvent.SpbhlMatchUrl, matchUrl, value => scheduledEvent.SpbhlMatchUrl = value);

            if (!string.IsNullOrWhiteSpace(match.ArenaName))
            {
                var arenaName = match.ArenaName.Trim();
                changed |= SetIfDifferent(scheduledEvent.LocationName, arenaName, value => scheduledEvent.LocationName = value);
            }

            if (match.HomeScore.HasValue)
            {
                changed |= SetIfDifferent(scheduledEvent.HomeScore, match.HomeScore, value => scheduledEvent.HomeScore = value);
            }

            if (match.AwayScore.HasValue)
            {
                changed |= SetIfDifferent(scheduledEvent.AwayScore, match.AwayScore, value => scheduledEvent.AwayScore = value);
            }

            if (match.Status == SpbhlMatchStatus.Finished && scheduledEvent.Status != EventStatus.Completed)
            {
                scheduledEvent.Status = EventStatus.Completed;
                changed = true;
            }
            else if (match.Status == SpbhlMatchStatus.Scheduled && scheduledEvent.Status != EventStatus.Completed)
            {
                changed |= SetIfDifferent(
                    scheduledEvent.Status,
                    EventStatus.Scheduled,
                    value => scheduledEvent.Status = value);
            }

            return changed;
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

        private static string BuildTitle(string homeTeamName, string awayTeamName)
        {
            return $"{homeTeamName} — {awayTeamName}";
        }
    }
}
