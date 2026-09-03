using AngleSharp.Dom;
using AngleSharp.Html.Parser;
using HockeyPlanner.Backend.WebAPI.Models.Spbhl;

namespace HockeyPlanner.Backend.WebAPI.Services
{
    public class SpbhlTeamProfileHtmlParser : ISpbhlTeamProfileHtmlParser
    {
        public SpbhlTeamProfile? ParseTeamProfile(string html, Guid teamId)
        {
            if (string.IsNullOrWhiteSpace(html) || teamId == Guid.Empty)
            {
                return null;
            }

            try
            {
                var document = new HtmlParser().ParseDocument(html);
                var logo = document.QuerySelector("img[src*='TableName=Team']");
                var card = logo?.Closest(".callout.secondary");
                var name = SpbhlHtmlParserUtilities.NormalizeText(card?.QuerySelector("h3")?.TextContent);
                if (card is null || string.IsNullOrWhiteSpace(name))
                {
                    return null;
                }

                var location = card.QuerySelector("h4.subheader");
                var city = location is null
                    ? null
                    : SpbhlHtmlParserUtilities.NormalizeText(string.Concat(
                        location.ChildNodes
                            .TakeWhile(node => node is not IElement element || !element.TagName.Equals("BR", StringComparison.OrdinalIgnoreCase))
                            .Where(node => node.NodeType == NodeType.Text)
                            .Select(node => node.TextContent)));
                var country = SpbhlHtmlParserUtilities.NormalizeText(location?.QuerySelector(".description")?.TextContent);
                var logoUri = SpbhlHtmlParserUtilities.NormalizeUrl(
                    logo?.GetAttribute("src"));

                return new SpbhlTeamProfile
                {
                    TeamId = teamId,
                    Name = name,
                    City = NullIfEmpty(city),
                    Country = NullIfEmpty(country),
                    DivisionName = null,
                    ProfileUrl = new Uri(SpbhlHtmlParserUtilities.BaseUri, $"Team?TeamID={teamId:D}").AbsoluteUri,
                    LogoUrl = logoUri?.AbsoluteUri,
                    CoverUrl = null
                };
            }
            catch (Exception exception) when (exception is FormatException or UriFormatException)
            {
                return null;
            }
        }

        private static string? NullIfEmpty(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;
    }
}
