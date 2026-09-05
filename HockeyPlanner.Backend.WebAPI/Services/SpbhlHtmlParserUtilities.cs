using System.Net;
using System.Text.RegularExpressions;

namespace HockeyPlanner.Backend.WebAPI.Services
{
    internal static class SpbhlHtmlParserUtilities
    {
        internal static readonly Uri BaseUri = new("https://spbhl.ru/");

        internal static string NormalizeText(string? value)
        {
            return Regex.Replace(WebUtility.HtmlDecode(value ?? string.Empty), @"\s+", " ").Trim();
        }

        internal static Uri? NormalizeUrl(string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return null;
            }

            return Uri.TryCreate(BaseUri, WebUtility.HtmlDecode(value), out var uri) ? uri : null;
        }

        internal static string? GetQueryValue(Uri uri, string name)
        {
            foreach (var part in uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
            {
                var pair = part.Split('=', 2);
                if (pair.Length == 2 && string.Equals(Uri.UnescapeDataString(pair[0]), name, StringComparison.OrdinalIgnoreCase))
                {
                    return Uri.UnescapeDataString(pair[1].Replace('+', ' '));
                }
            }

            return null;
        }

        internal static bool IsPage(Uri uri, string pageName)
        {
            var fileName = Path.GetFileNameWithoutExtension(uri.AbsolutePath);
            return string.Equals(fileName, pageName, StringComparison.OrdinalIgnoreCase);
        }
    }
}
