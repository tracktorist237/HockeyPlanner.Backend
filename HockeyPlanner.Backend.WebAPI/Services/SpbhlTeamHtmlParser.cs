using AngleSharp.Dom;
using AngleSharp.Html.Parser;
using HockeyPlanner.Backend.WebAPI.Models.Spbhl;

namespace HockeyPlanner.Backend.WebAPI.Services
{
    public class SpbhlTeamHtmlParser : ISpbhlTeamHtmlParser
    {
        public IReadOnlyCollection<SpbhlTeamSearchItem> ParseTeams(string html)
        {
            if (string.IsNullOrWhiteSpace(html))
            {
                return [];
            }

            var document = new HtmlParser().ParseDocument(html);
            var teams = new Dictionary<Guid, SpbhlTeamSearchItem>();

            foreach (var link in document.QuerySelectorAll("h4 a[href]"))
            {
                try
                {
                    var profileUri = SpbhlHtmlParserUtilities.NormalizeUrl(link.GetAttribute("href"));
                    if (profileUri is null || !SpbhlHtmlParserUtilities.IsPage(profileUri, "Team"))
                    {
                        continue;
                    }

                    var teamIdText = SpbhlHtmlParserUtilities.GetQueryValue(profileUri, "TeamID");
                    var name = SpbhlHtmlParserUtilities.NormalizeText(link.TextContent);
                    if (!Guid.TryParse(teamIdText, out var teamId) || string.IsNullOrWhiteSpace(name) || teams.ContainsKey(teamId))
                    {
                        continue;
                    }

                    var card = link.Closest(".callout") ?? link.ParentElement;
                    var location = card?.QuerySelector("p");
                    var city = location is null
                        ? null
                        : SpbhlHtmlParserUtilities.NormalizeText(string.Concat(
                            location.ChildNodes.Where(node => node.NodeType == NodeType.Text).Select(node => node.TextContent)));
                    var country = SpbhlHtmlParserUtilities.NormalizeText(location?.QuerySelector(".description")?.TextContent);
                    var logoUri = SpbhlHtmlParserUtilities.NormalizeUrl(card?.QuerySelector("img[src]")?.GetAttribute("src"));
                    var tournamentIdText = SpbhlHtmlParserUtilities.GetQueryValue(profileUri, "TournamentID");

                    teams[teamId] = new SpbhlTeamSearchItem
                    {
                        TeamId = teamId,
                        Name = name,
                        City = NullIfEmpty(city),
                        Country = NullIfEmpty(country),
                        LogoUrl = logoUri?.AbsoluteUri,
                        ProfileUrl = profileUri.AbsoluteUri,
                        TournamentId = int.TryParse(tournamentIdText, out var tournamentId) ? tournamentId : null,
                        DivisionName = FindDivisionName(link)
                    };
                }
                catch (Exception exception) when (exception is FormatException or UriFormatException)
                {
                    // A malformed card must not prevent parsing the remaining teams.
                }
            }

            return teams.Values.ToArray();
        }

        private static string? FindDivisionName(IElement link)
        {
            var current = link.ParentElement;
            while (current is not null && !current.ClassList.Contains("grid-x"))
            {
                current = current.ParentElement;
            }

            var sibling = current?.PreviousElementSibling;
            while (sibling is not null)
            {
                if (string.Equals(sibling.TagName, "H3", StringComparison.OrdinalIgnoreCase))
                {
                    return NullIfEmpty(SpbhlHtmlParserUtilities.NormalizeText(sibling.TextContent));
                }

                sibling = sibling.PreviousElementSibling;
            }

            return null;
        }

        private static string? NullIfEmpty(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;
    }
}
