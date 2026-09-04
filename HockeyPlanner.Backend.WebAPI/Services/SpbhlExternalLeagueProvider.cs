using HockeyPlanner.Backend.Core.Enums;
using HockeyPlanner.Backend.Core.Exceptions;
using HockeyPlanner.Backend.WebAPI.Models.ExternalLeagues;
using HockeyPlanner.Backend.WebAPI.Models.Spbhl;

namespace HockeyPlanner.Backend.WebAPI.Services
{
    public sealed class SpbhlExternalLeagueProvider(ISpbhlClient client) : IExternalLeagueProvider
    {
        public ExternalLeagueProvider Provider => ExternalLeagueProvider.Spbhl;

        public async Task<IReadOnlyCollection<ExternalTeamSearchItem>> SearchTeamsAsync(
            string title,
            CancellationToken cancellationToken)
        {
            var teams = await client.SearchTeamsAsync(title, cancellationToken);
            return teams.Select(MapSearchItem).ToArray();
        }

        public async Task<ExternalTeamProfile?> GetTeamProfileAsync(
            string externalTeamId,
            CancellationToken cancellationToken)
        {
            if (!Guid.TryParse(externalTeamId, out var teamId) || teamId == Guid.Empty)
            {
                throw new BusinessRuleException("Некорректный идентификатор команды СПбХЛ.");
            }

            var profile = await client.GetTeamProfileAsync(teamId, cancellationToken);
            return profile is null ? null : new ExternalTeamProfile
            {
                Provider = Provider,
                ExternalTeamId = profile.TeamId.ToString("D"),
                Name = profile.Name,
                City = profile.City,
                Country = profile.Country,
                LogoUrl = profile.LogoUrl,
                CoverUrl = profile.CoverUrl,
                ProfileUrl = profile.ProfileUrl,
                DivisionName = profile.DivisionName,
                FoundedYear = profile.FoundedYear,
                CoachName = profile.CoachName,
                AdministratorName = profile.AdministratorName,
                Phones = profile.Phones.Select(value => new ExternalContactCandidate
                {
                    Value = value,
                    Label = string.IsNullOrWhiteSpace(profile.AdministratorName)
                        ? "Официальный контакт"
                        : "Администратор"
                }).ToArray(),
                WebsiteUrls = profile.WebsiteUrls.Select(value => new ExternalContactCandidate
                {
                    Value = value,
                    Label = "Сайт команды"
                }).ToArray()
            };
        }

        public async Task<IReadOnlyCollection<ExternalMatch>> GetTeamScheduleAsync(
            string externalTeamId,
            CancellationToken cancellationToken)
        {
            if (!Guid.TryParse(externalTeamId, out var teamId) || teamId == Guid.Empty)
            {
                throw new BusinessRuleException("Некорректный идентификатор команды СПбХЛ.");
            }

            var matches = await client.GetTeamScheduleAsync(teamId, cancellationToken);
            return matches.Select(MapMatch).ToArray();
        }

        public async Task<ExternalMatchDetails?> GetMatchDetailsAsync(
            string externalCompetitionId,
            string externalMatchId,
            CancellationToken cancellationToken)
        {
            if (!int.TryParse(externalCompetitionId, out var tournamentId) ||
                !int.TryParse(externalMatchId, out var matchId))
            {
                throw new BusinessRuleException("Некорректный идентификатор матча СПбХЛ.");
            }

            var details = await client.GetMatchDetailsAsync(tournamentId, matchId, cancellationToken);
            return details is null ? null : new ExternalMatchDetails
            {
                ExternalCompetitionId = details.TournamentId.ToString(),
                ExternalMatchId = details.MatchId.ToString(),
                HomeScore = details.HomeScore,
                AwayScore = details.AwayScore,
                Status = MapStatus(details.Status),
                ArenaName = details.ArenaName,
                ArenaAddress = details.ArenaAddress,
                TournamentName = details.TournamentName,
                DivisionName = details.DivisionName
            };
        }

        private ExternalTeamSearchItem MapSearchItem(SpbhlTeamSearchItem team) => new()
        {
            Provider = Provider,
            ExternalTeamId = team.TeamId.ToString("D"),
            Name = team.Name,
            City = team.City,
            Country = team.Country,
            LogoUrl = team.LogoUrl,
            ProfileUrl = team.ProfileUrl,
            DivisionName = team.DivisionName
        };

        private static ExternalMatch MapMatch(SpbhlMatchItem match) => new()
        {
            ExternalCompetitionId = match.TournamentId.ToString(),
            ExternalMatchId = match.MatchId.ToString(),
            LegacyNumericCompetitionId = match.TournamentId,
            LegacyNumericMatchId = match.MatchId,
            StartTime = match.StartTime,
            HomeTeamName = match.HomeTeamName,
            AwayTeamName = match.AwayTeamName,
            ArenaName = match.ArenaName,
            ArenaAddress = match.ArenaAddress,
            HomeScore = match.HomeScore,
            AwayScore = match.AwayScore,
            Status = MapStatus(match.Status),
            TournamentName = match.TournamentName,
            DivisionName = match.DivisionName,
            MatchUrl = match.MatchUrl
        };

        private static ExternalMatchStatus MapStatus(SpbhlMatchStatus status) => status switch
        {
            SpbhlMatchStatus.Scheduled => ExternalMatchStatus.Scheduled,
            SpbhlMatchStatus.Finished => ExternalMatchStatus.Finished,
            SpbhlMatchStatus.Rescheduled => ExternalMatchStatus.Rescheduled,
            SpbhlMatchStatus.Cancelled => ExternalMatchStatus.Cancelled,
            _ => ExternalMatchStatus.Unknown
        };
    }
}
