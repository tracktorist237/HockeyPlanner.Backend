using HockeyPlanner.Backend.WebAPI.Models.Spbhl;
using System.Net;
using System.Text.RegularExpressions;

namespace HockeyPlanner.Backend.WebAPI.Services
{
    public class SpbhlPlayerSearchService : ISpbhlPlayerSearchService
    {
        private const string SpbhlBaseUrl = "https://spbhl.ru";
        private const string PlayersPath = "/Players";
        private const string PlayersGridEventTarget = "ctl00$MainContent$PlayersGridView";

        public SpbhlPlayerSearchService() { }

        public async Task<SpbhlPlayersSearchResponse> SearchPlayers(
            string fullName,
            string? birthYear,
            int page,
            CancellationToken cancellationToken)
        {
            var safePage = Math.Max(1, page);
            var safeFullName = fullName?.Trim() ?? string.Empty;
            var safeBirthYear = birthYear?.Trim() ?? string.Empty;

            var url = BuildSearchUrl(safeFullName, safeBirthYear);

            var handler = new HttpClientHandler
            {
                CookieContainer = new CookieContainer(),
                UseCookies = true
            };

            using var client = new HttpClient(handler);
            client.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (compatible; HockeyPlanner/1.0)");

            var html = await LoadPageHtml(client, url, safePage, cancellationToken);
            var players = ParsePlayers(html);
            var totalPages = ParseTotalPages(html);

            return new SpbhlPlayersSearchResponse
            {
                Page = safePage,
                TotalPages = totalPages,
                Players = players
            };
        }

        private static string BuildSearchUrl(string fullName, string birthYear)
        {
            var encodedFullName = Uri.EscapeDataString(fullName);
            var encodedBirthYear = Uri.EscapeDataString(birthYear);
            return $"{SpbhlBaseUrl}{PlayersPath}?SeasonID=0&BirthYear={encodedBirthYear}&FullName={encodedFullName}";
        }

        private static async Task<string> LoadPageHtml(
            HttpClient client,
            string searchUrl,
            int page,
            CancellationToken cancellationToken)
        {
            var firstPageResponse = await client.GetAsync(searchUrl, cancellationToken);
            firstPageResponse.EnsureSuccessStatusCode();
            var firstPageHtml = await firstPageResponse.Content.ReadAsStringAsync(cancellationToken);

            if (page == 1)
            {
                return firstPageHtml;
            }

            var hiddenInputs = ExtractHiddenInputs(firstPageHtml);
            var formData = new List<KeyValuePair<string, string>>
            {
                new("__EVENTTARGET", PlayersGridEventTarget),
                new("__EVENTARGUMENT", $"Page${page}")
            };

            foreach (var hiddenInput in hiddenInputs)
            {
                formData.Add(new KeyValuePair<string, string>(hiddenInput.Key, hiddenInput.Value));
            }

            using var postContent = new FormUrlEncodedContent(formData);
            var pagedResponse = await client.PostAsync(searchUrl, postContent, cancellationToken);
            pagedResponse.EnsureSuccessStatusCode();
            return await pagedResponse.Content.ReadAsStringAsync(cancellationToken);
        }

        private static IReadOnlyCollection<SpbhlPlayerSearchItem> ParsePlayers(string html)
        {
            var rowMatches = Regex.Matches(
                html,
                @"<tr>\s*<td>\s*<a id=""PlayerPhotoHyperLink"".*?</tr>",
                RegexOptions.Singleline | RegexOptions.IgnoreCase);

            var result = new List<SpbhlPlayerSearchItem>();
            foreach (Match rowMatch in rowMatches)
            {
                var rowHtml = rowMatch.Value;
                var playerIdRaw = ExtractValue(rowHtml, @"PlayerID=(?<value>[a-f0-9\-]{36})");

                if (!Guid.TryParse(playerIdRaw, out var playerId))
                {
                    continue;
                }

                var fullName = WebUtility.HtmlDecode(
                    ExtractValue(rowHtml, @"<a id=""PlayerHyperLink""[^>]*>(?<value>.*?)</a>") ?? string.Empty).Trim();
                var birthDate = WebUtility.HtmlDecode(
                    ExtractValue(rowHtml, @"<td align=""center"">\s*(?<value>\d{2}\.\d{2}\.\d{4})"));
                var teamName = WebUtility.HtmlDecode(
                    ExtractValue(rowHtml, @"<a id=""TeamHyperLink""[^>]*>(?<value>.*?)</a>") ?? string.Empty).Trim();
                var jerseyRaw = ExtractValue(rowHtml, @"<span class=""label"">№<b>(?<value>\d+)</b>");

                int? jerseyNumber = null;
                if (int.TryParse(jerseyRaw, out var parsedJersey))
                {
                    jerseyNumber = parsedJersey;
                }

                result.Add(new SpbhlPlayerSearchItem
                {
                    PlayerId = playerId,
                    FullName = fullName,
                    BirthDate = string.IsNullOrWhiteSpace(birthDate) ? null : birthDate,
                    TeamName = string.IsNullOrWhiteSpace(teamName) ? null : teamName,
                    JerseyNumber = jerseyNumber,
                    PhotoSmallUrl = $"{SpbhlBaseUrl}/ImageHandler.ashx?ID={playerId}&Size=M&TableName=Player",
                    PhotoLargeUrl = $"{SpbhlBaseUrl}/ImageHandler.ashx?ID={playerId}&Size=O&TableName=Player",
                    ProfileUrl = $"{SpbhlBaseUrl}/Player?PlayerID={playerId}"
                });
            }

            return result;
        }

        private static int ParseTotalPages(string html)
        {
            var currentPageRaw = ExtractValue(html, @"<span class=""current-page"">(?<value>\d+)</span>");
            var currentPage = 1;
            if (int.TryParse(currentPageRaw, out var parsedCurrentPage) && parsedCurrentPage > 0)
            {
                currentPage = parsedCurrentPage;
            }

            var pageMatches = Regex.Matches(html, @"Page\$(?<page>\d+)", RegexOptions.IgnoreCase);
            var maxPage = currentPage;
            foreach (Match pageMatch in pageMatches)
            {
                var pageRaw = pageMatch.Groups["page"].Value;
                if (int.TryParse(pageRaw, out var parsedPage) && parsedPage > maxPage)
                {
                    maxPage = parsedPage;
                }
            }

            return Math.Max(1, maxPage);
        }

        private static Dictionary<string, string> ExtractHiddenInputs(string html)
        {
            var names = new[] { "__VIEWSTATE", "__VIEWSTATEGENERATOR", "__EVENTVALIDATION", "__LASTFOCUS" };
            var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            foreach (var name in names)
            {
                var pattern = $@"name=""{Regex.Escape(name)}""[^>]*value=""(?<value>.*?)""";
                var value = ExtractValue(html, pattern);
                if (!string.IsNullOrEmpty(value))
                {
                    values[name] = WebUtility.HtmlDecode(value);
                }
            }

            return values;
        }

        private static string? ExtractValue(string source, string pattern)
        {
            var match = Regex.Match(source, pattern, RegexOptions.Singleline | RegexOptions.IgnoreCase);
            return match.Success ? match.Groups["value"].Value : null;
        }
    }
}
