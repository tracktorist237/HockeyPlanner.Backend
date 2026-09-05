using AngleSharp.Dom;
using AngleSharp.Html.Parser;
using HockeyPlanner.Backend.WebAPI.Models.Spbhl;
using System.Text.RegularExpressions;

namespace HockeyPlanner.Backend.WebAPI.Services
{
    public class SpbhlTeamProfileHtmlParser : ISpbhlTeamProfileHtmlParser
    {
        private static readonly Regex PhonePattern = new(
            @"(?<!\d)(?:\+7|8)\s*\(?\d{3}\)?(?:[\s-]*\d){7}(?!\d)",
            RegexOptions.Compiled);

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
                var coverUri = ParseCoverUrl(document);
                var fields = card.QuerySelectorAll("tr")
                    .Select(row => row.QuerySelectorAll("td").ToArray())
                    .Where(cells => cells.Length >= 2)
                    .Select(cells => new
                    {
                        Label = NormalizeLabel(cells[0].TextContent),
                        ValueCell = cells[1]
                    })
                    .Where(field => !string.IsNullOrWhiteSpace(field.Label))
                    .GroupBy(field => field.Label, StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(group => group.Key, group => group.First().ValueCell, StringComparer.OrdinalIgnoreCase);

                fields.TryGetValue("год создания", out var foundedCell);
                fields.TryGetValue("тренер", out var coachCell);
                fields.TryGetValue("администратор", out var administratorCell);
                fields.TryGetValue("контакты", out var contactsCell);
                fields.TryGetValue("веб", out var websiteCell);

                return new SpbhlTeamProfile
                {
                    TeamId = teamId,
                    Name = name,
                    City = NullIfEmpty(city),
                    Country = NullIfEmpty(country),
                    DivisionName = null,
                    ProfileUrl = new Uri(SpbhlHtmlParserUtilities.BaseUri, $"Team?TeamID={teamId:D}").AbsoluteUri,
                    LogoUrl = logoUri?.AbsoluteUri,
                    CoverUrl = coverUri?.AbsoluteUri,
                    FoundedYear = ParseFoundedYear(foundedCell?.TextContent),
                    CoachName = NullIfEmpty(SpbhlHtmlParserUtilities.NormalizeText(coachCell?.TextContent)),
                    AdministratorName = NullIfEmpty(SpbhlHtmlParserUtilities.NormalizeText(administratorCell?.TextContent)),
                    Phones = ParsePhones(contactsCell?.TextContent),
                    WebsiteUrls = ParseWebsites(websiteCell)
                };
            }
            catch (Exception exception) when (exception is FormatException or UriFormatException)
            {
                return null;
            }
        }

        private static string? NullIfEmpty(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;

        private static Uri? ParseCoverUrl(IDocument document)
        {
            var photoHeading = document.QuerySelectorAll("h4")
                .FirstOrDefault(element =>
                    string.Equals(
                        SpbhlHtmlParserUtilities.NormalizeText(element.TextContent),
                        "Фото",
                        StringComparison.OrdinalIgnoreCase));
            var photo = photoHeading?.NextElementSibling;
            while (photo is not null && !photo.Matches(".afigure") && !photo.Matches("h4"))
            {
                photo = photo.NextElementSibling;
            }
            if (photo is null || !photo.Matches(".afigure"))
            {
                return null;
            }

            var photoLink = photo.QuerySelector(".afigure-pic a[href]")?.GetAttribute("href");
            var photoSource = photo.QuerySelector(".afigure-pic img[src]")?.GetAttribute("src");
            return SpbhlHtmlParserUtilities.NormalizeUrl(photoLink ?? photoSource);
        }

        private static string NormalizeLabel(string? value) =>
            SpbhlHtmlParserUtilities.NormalizeText(value).TrimEnd(':').Trim();

        private static int? ParseFoundedYear(string? value)
        {
            var normalized = SpbhlHtmlParserUtilities.NormalizeText(value);
            return int.TryParse(normalized, out var year) && year is >= 1800 and <= 2200 ? year : null;
        }

        private static IReadOnlyCollection<string> ParsePhones(string? value)
        {
            var normalized = SpbhlHtmlParserUtilities.NormalizeText(value);
            return PhonePattern.Matches(normalized)
                .Select(match => NormalizePhone(match.Value))
                .Where(phone => phone is not null)
                .Cast<string>()
                .Distinct(StringComparer.Ordinal)
                .ToArray();
        }

        private static string? NormalizePhone(string value)
        {
            var digits = new string(value.Where(char.IsDigit).ToArray());
            if (digits.Length != 11 || digits[0] is not ('7' or '8'))
            {
                return null;
            }
            var prefix = value.TrimStart().StartsWith('+') ? "+7" : "8";
            return $"{prefix} ({digits[1..4]}) {digits[4..7]}-{digits[7..9]}-{digits[9..11]}";
        }

        private static IReadOnlyCollection<string> ParseWebsites(IElement? cell)
        {
            if (cell is null)
            {
                return Array.Empty<string>();
            }

            var candidates = cell.QuerySelectorAll("a")
                .Select(anchor => anchor.GetAttribute("href") ?? anchor.TextContent)
                .Append(cell.TextContent)
                .SelectMany(value => SpbhlHtmlParserUtilities.NormalizeText(value)
                    .Split([',', ';', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                .Select(NormalizeWebsite)
                .Where(url => url is not null)
                .Cast<string>()
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            return candidates;
        }

        private static string? NormalizeWebsite(string value)
        {
            var candidate = value.Trim().TrimEnd('.', ',', ';');
            if (!candidate.Contains('.', StringComparison.Ordinal) || candidate.Any(char.IsWhiteSpace))
            {
                return null;
            }
            if (!candidate.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
                !candidate.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                candidate = $"https://{candidate}";
            }
            return Uri.TryCreate(candidate, UriKind.Absolute, out var uri) &&
                   uri.Scheme is "http" or "https" &&
                   !string.IsNullOrWhiteSpace(uri.Host)
                ? uri.AbsoluteUri.TrimEnd('/')
                : null;
        }
    }
}
