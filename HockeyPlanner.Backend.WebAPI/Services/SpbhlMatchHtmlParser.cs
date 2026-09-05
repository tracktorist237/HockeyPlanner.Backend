using System.Text.RegularExpressions;
using AngleSharp.Dom;
using AngleSharp.Html.Parser;
using HockeyPlanner.Backend.WebAPI.Models.Spbhl;

namespace HockeyPlanner.Backend.WebAPI.Services
{
    public class SpbhlMatchHtmlParser : ISpbhlMatchHtmlParser
    {
        private static readonly Regex ScorePattern = new(@"^(?<home>\d+)\s*:\s*(?<away>\d+)$", RegexOptions.Compiled);
        private static readonly Regex TournamentDivisionPattern = new(
            @"^(?<tournament>.+?)\.\s*Дивизион\s*«(?<division>[^»]+)»$",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        public SpbhlMatchDetails? ParseMatch(string html, int tournamentId, int matchId)
        {
            if (string.IsNullOrWhiteSpace(html) || tournamentId <= 0 || matchId <= 0)
            {
                return null;
            }

            try
            {
                var document = new HtmlParser().ParseDocument(html);
                var center = document.QuerySelector(".large-4.cell.text-center");
                var matchGrid = center?.Closest(".grid-x.grid-padding-x");
                var teamNames = matchGrid?.QuerySelectorAll(".callout.match h4 a")
                    .Select(link => SpbhlHtmlParserUtilities.NormalizeText(link.TextContent))
                    .Where(name => !string.IsNullOrWhiteSpace(name))
                    .Take(2)
                    .ToArray() ?? [];
                if (teamNames.Length != 2 || center is null)
                {
                    return null;
                }

                var scoreText = SpbhlHtmlParserUtilities.NormalizeText(center.QuerySelector("h1 b")?.TextContent);
                var hasScore = TryParseScore(scoreText, out var homeScore, out var awayScore);
                var rawStatus = SpbhlHtmlParserUtilities.NormalizeText(
                    center.QuerySelector("span.success.label, span.alert.label, span.warning.label")?.TextContent);
                var status = ParseStatus(rawStatus, hasScore);
                var arenaLink = center.QuerySelector("a[href*='Arena']");
                var arenaName = SpbhlHtmlParserUtilities.NormalizeText(arenaLink?.GetAttribute("title"));
                if (string.IsNullOrWhiteSpace(arenaName))
                {
                    arenaName = SpbhlHtmlParserUtilities.NormalizeText(arenaLink?.TextContent);
                }

                var tournamentHeading = SpbhlHtmlParserUtilities.NormalizeText(
                    document.QuerySelector(".callout.tournament h3")?.TextContent);
                ParseTournament(tournamentHeading, out var tournamentName, out var divisionName);

                return new SpbhlMatchDetails
                {
                    TournamentId = tournamentId,
                    MatchId = matchId,
                    HomeTeamName = teamNames[0],
                    AwayTeamName = teamNames[1],
                    HomeScore = hasScore ? homeScore : null,
                    AwayScore = hasScore ? awayScore : null,
                    Status = status,
                    ArenaName = NullIfEmpty(arenaName),
                    ArenaAddress = NullIfEmpty(SpbhlHtmlParserUtilities.NormalizeText(
                        arenaLink?.Closest("p")?.QuerySelector(".description")?.TextContent)),
                    TournamentName = tournamentName,
                    DivisionName = divisionName,
                    MatchUrl = new Uri(SpbhlHtmlParserUtilities.BaseUri,
                        $"Match?TournamentID={tournamentId}&MatchID={matchId}").AbsoluteUri
                };
            }
            catch (Exception exception) when (exception is FormatException or UriFormatException)
            {
                return null;
            }
        }

        private static bool TryParseScore(string value, out int homeScore, out int awayScore)
        {
            var match = ScorePattern.Match(value);
            homeScore = 0;
            awayScore = 0;
            return match.Success &&
                   int.TryParse(match.Groups["home"].Value, out homeScore) &&
                   int.TryParse(match.Groups["away"].Value, out awayScore);
        }

        private static SpbhlMatchStatus ParseStatus(string rawStatus, bool hasScore)
        {
            if (rawStatus.Equals("Завершен", StringComparison.OrdinalIgnoreCase) ||
                rawStatus.Equals("Завершён", StringComparison.OrdinalIgnoreCase))
            {
                return SpbhlMatchStatus.Finished;
            }

            if (rawStatus.Equals("Перенесен", StringComparison.OrdinalIgnoreCase) ||
                rawStatus.Equals("Перенесён", StringComparison.OrdinalIgnoreCase))
            {
                return SpbhlMatchStatus.Rescheduled;
            }

            if (rawStatus.Equals("Отменен", StringComparison.OrdinalIgnoreCase) ||
                rawStatus.Equals("Отменён", StringComparison.OrdinalIgnoreCase))
            {
                return SpbhlMatchStatus.Cancelled;
            }

            return SpbhlMatchStatus.Unknown;
        }

        private static void ParseTournament(string value, out string? tournamentName, out string? divisionName)
        {
            var match = TournamentDivisionPattern.Match(value);
            tournamentName = NullIfEmpty(match.Success ? match.Groups["tournament"].Value : value);
            divisionName = match.Success ? NullIfEmpty(match.Groups["division"].Value) : null;
        }

        private static string? NullIfEmpty(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }
}
