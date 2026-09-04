using System.Globalization;
using System.Text.RegularExpressions;
using AngleSharp.Dom;
using AngleSharp.Html.Parser;
using HockeyPlanner.Backend.WebAPI.Models.Spbhl;

namespace HockeyPlanner.Backend.WebAPI.Services
{
    public class SpbhlScheduleHtmlParser : ISpbhlScheduleHtmlParser
    {
        private static readonly Regex DatePattern = new(@"\b(?<date>\d{2}\.\d{2}\.\d{4})\b", RegexOptions.Compiled);
        private static readonly Regex TimePattern = new(@"\b(?<time>\d{2}:\d{2})\b", RegexOptions.Compiled);
        private static readonly Regex TeamSeparatorPattern = new(@"\s+-\s+", RegexOptions.Compiled);
        private static readonly Regex ScorePattern = new(@"^(?<home>\d+)\s*:\s*(?<away>\d+)$", RegexOptions.Compiled);
        private static readonly Regex TournamentWithDivisionPattern = new(
            @"^(?<tournament>.+?)\s*\((?<division>[^()]+)\)$",
            RegexOptions.Compiled);
        private static readonly TimeZoneInfo MoscowTimeZone = TimeZoneInfo.FindSystemTimeZoneById("Europe/Moscow");
        public IReadOnlyCollection<SpbhlMatchItem> ParseSchedule(string html)
        {
            if (string.IsNullOrWhiteSpace(html))
            {
                return [];
            }

            var document = new HtmlParser().ParseDocument(html);
            var matches = new Dictionary<(int TournamentId, int MatchId), SpbhlMatchItem>();

            foreach (var row in document.QuerySelectorAll("table#MatchGridView tr"))
            {
                try
                {
                    var cells = row.Children.Where(element => string.Equals(element.TagName, "TD", StringComparison.OrdinalIgnoreCase)).ToArray();
                    if (cells.Length < 8)
                    {
                        continue;
                    }

                    var matchLink = cells[6].QuerySelector("a[href]");
                    var matchUri = SpbhlHtmlParserUtilities.NormalizeUrl(matchLink?.GetAttribute("href"));
                    if (matchUri is null || !SpbhlHtmlParserUtilities.IsPage(matchUri, "Match"))
                    {
                        continue;
                    }

                    if (!int.TryParse(SpbhlHtmlParserUtilities.GetQueryValue(matchUri, "MatchID"), out var matchId) ||
                        !int.TryParse(SpbhlHtmlParserUtilities.GetQueryValue(matchUri, "TournamentID"), out var tournamentId) ||
                        !TryParseStartTime(cells[3].TextContent, cells[4].TextContent, out var startTime) ||
                        !TryParseTeams(matchLink?.TextContent, out var homeTeamName, out var awayTeamName))
                    {
                        continue;
                    }

                    var key = (tournamentId, matchId);
                    if (matches.ContainsKey(key))
                    {
                        continue;
                    }

                    var arenaLink = cells[5].QuerySelector("a[href]");
                    var arenaUri = SpbhlHtmlParserUtilities.NormalizeUrl(arenaLink?.GetAttribute("href"));
                    var arenaName = SpbhlHtmlParserUtilities.NormalizeText(arenaLink?.GetAttribute("title"));
                    if (string.IsNullOrWhiteSpace(arenaName))
                    {
                        arenaName = SpbhlHtmlParserUtilities.NormalizeText(arenaLink?.TextContent);
                    }
                    var arenaAddress = NullIfEmpty(SpbhlHtmlParserUtilities.NormalizeText(
                        arenaLink?.Closest("p")?.QuerySelector(".description")?.TextContent));
                    var tournamentText = SpbhlHtmlParserUtilities.NormalizeText(
                        cells[0].QuerySelector("a[href] b")?.TextContent ?? cells[0].QuerySelector("a[href]")?.TextContent);
                    ParseTournament(tournamentText, out var tournamentName, out var divisionName);

                    var scoreText = SpbhlHtmlParserUtilities.NormalizeText(cells[7].TextContent);
                    var hasScore = TryParseScore(scoreText, out var homeScore, out var awayScore);
                    var reportLink = cells.Skip(8)
                        .SelectMany(cell => cell.QuerySelectorAll("a[href]"))
                        .FirstOrDefault(anchor =>
                            string.Equals(anchor.GetAttribute("title"), "Протокол матча", StringComparison.OrdinalIgnoreCase) ||
                            anchor.ClassList.Contains("summary"));
                    var explicitStatus = ParseExplicitStatus(scoreText);
                    var isFinished = hasScore && reportLink is not null;
                    var rawStatus = explicitStatus != SpbhlMatchStatus.Unknown
                        ? scoreText
                        : isFinished
                            ? SpbhlHtmlParserUtilities.NormalizeText(reportLink!.GetAttribute("title"))
                            : null;

                    matches[key] = new SpbhlMatchItem
                    {
                        MatchId = matchId,
                        TournamentId = tournamentId,
                        StartTime = startTime,
                        HomeTeamId = null,
                        HomeTeamName = homeTeamName,
                        AwayTeamId = null,
                        AwayTeamName = awayTeamName,
                        ArenaName = string.IsNullOrWhiteSpace(arenaName) ? null : arenaName,
                        ArenaAddress = arenaAddress,
                        ArenaId = TryGetGuidQueryValue(arenaUri, "ArenaID"),
                        TournamentName = tournamentName,
                        DivisionName = divisionName,
                        HomeScore = hasScore ? homeScore : null,
                        AwayScore = hasScore ? awayScore : null,
                        Status = explicitStatus != SpbhlMatchStatus.Unknown
                            ? explicitStatus
                            : isFinished ? SpbhlMatchStatus.Finished : SpbhlMatchStatus.Unknown,
                        RawStatus = string.IsNullOrWhiteSpace(rawStatus) ? null : rawStatus,
                        MatchUrl = matchUri.AbsoluteUri
                    };
                }
                catch (Exception exception) when (exception is FormatException or UriFormatException)
                {
                    // A malformed row must not prevent parsing the remaining schedule.
                }
            }

            return matches.Values.ToArray();
        }

        private static void ParseTournament(string value, out string? tournamentName, out string? divisionName)
        {
            var match = TournamentWithDivisionPattern.Match(value);
            if (match.Success)
            {
                tournamentName = NullIfEmpty(SpbhlHtmlParserUtilities.NormalizeText(match.Groups["tournament"].Value));
                divisionName = NullIfEmpty(SpbhlHtmlParserUtilities.NormalizeText(match.Groups["division"].Value));
                return;
            }

            tournamentName = NullIfEmpty(value);
            divisionName = null;
        }

        private static bool TryParseStartTime(string dateText, string timeText, out DateTimeOffset startTime)
        {
            var dateMatch = DatePattern.Match(SpbhlHtmlParserUtilities.NormalizeText(dateText));
            var timeMatch = TimePattern.Match(SpbhlHtmlParserUtilities.NormalizeText(timeText));
            if (!dateMatch.Success || !timeMatch.Success ||
                !DateTime.TryParseExact(
                    $"{dateMatch.Groups["date"].Value} {timeMatch.Groups["time"].Value}",
                    "dd.MM.yyyy HH:mm",
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.None,
                    out var localTime))
            {
                startTime = default;
                return false;
            }

            localTime = DateTime.SpecifyKind(localTime, DateTimeKind.Unspecified);
            startTime = new DateTimeOffset(localTime, MoscowTimeZone.GetUtcOffset(localTime));
            return true;
        }

        private static bool TryParseTeams(string? value, out string homeTeamName, out string awayTeamName)
        {
            var parts = TeamSeparatorPattern.Split(SpbhlHtmlParserUtilities.NormalizeText(value), 2);
            homeTeamName = parts.Length > 0 ? parts[0] : string.Empty;
            awayTeamName = parts.Length > 1 ? parts[1] : string.Empty;
            return !string.IsNullOrWhiteSpace(homeTeamName) && !string.IsNullOrWhiteSpace(awayTeamName);
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

        private static Guid? TryGetGuidQueryValue(Uri? uri, string name)
        {
            return uri is not null && Guid.TryParse(SpbhlHtmlParserUtilities.GetQueryValue(uri, name), out var value)
                ? value
                : null;
        }

        private static SpbhlMatchStatus ParseExplicitStatus(string value)
        {
            if (value.Equals("Перенесён", StringComparison.OrdinalIgnoreCase) ||
                value.Equals("Перенесен", StringComparison.OrdinalIgnoreCase))
            {
                return SpbhlMatchStatus.Rescheduled;
            }
            if (value.Equals("Отменён", StringComparison.OrdinalIgnoreCase) ||
                value.Equals("Отменен", StringComparison.OrdinalIgnoreCase))
            {
                return SpbhlMatchStatus.Cancelled;
            }
            return SpbhlMatchStatus.Unknown;
        }

        private static string? NullIfEmpty(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;
    }
}
