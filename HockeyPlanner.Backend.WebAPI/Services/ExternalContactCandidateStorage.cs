using HockeyPlanner.Backend.WebAPI.Models.ExternalLeagues;
using System.Text.Json;

namespace HockeyPlanner.Backend.WebAPI.Services
{
    internal static class ExternalContactCandidateStorage
    {
        public static IReadOnlyCollection<ExternalContactCandidate> Deserialize(string? json, string fallbackLabel)
        {
            if (string.IsNullOrWhiteSpace(json))
            {
                return Array.Empty<ExternalContactCandidate>();
            }

            try
            {
                using var document = JsonDocument.Parse(json);
                if (document.RootElement.ValueKind != JsonValueKind.Array)
                {
                    return Array.Empty<ExternalContactCandidate>();
                }

                return document.RootElement.EnumerateArray()
                    .Select(element => Parse(element, fallbackLabel))
                    .Where(candidate => candidate is not null)
                    .Cast<ExternalContactCandidate>()
                    .ToArray();
            }
            catch (JsonException)
            {
                return Array.Empty<ExternalContactCandidate>();
            }
        }

        public static string? Merge(
            string? json,
            IEnumerable<ExternalContactCandidate> additions,
            string fallbackLabel,
            Func<string, string> normalize)
        {
            var values = Deserialize(json, fallbackLabel).ToList();
            var indexes = values
                .Select((value, index) => new { Key = normalize(value.Value), Index = index })
                .Where(value => !string.IsNullOrWhiteSpace(value.Key))
                .GroupBy(value => value.Key, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => group.First().Index, StringComparer.OrdinalIgnoreCase);

            foreach (var addition in additions.Where(value => !string.IsNullOrWhiteSpace(value.Value)))
            {
                var normalized = normalize(addition.Value);
                var candidate = Normalize(addition, fallbackLabel);
                if (indexes.TryGetValue(normalized, out var index))
                {
                    if (string.Equals(values[index].Label, fallbackLabel, StringComparison.Ordinal) &&
                        !string.Equals(candidate.Label, fallbackLabel, StringComparison.Ordinal))
                    {
                        values[index] = candidate;
                    }
                    continue;
                }

                indexes[normalized] = values.Count;
                values.Add(candidate);
            }

            return values.Count == 0 ? null : JsonSerializer.Serialize(values);
        }

        private static ExternalContactCandidate? Parse(JsonElement element, string fallbackLabel)
        {
            if (element.ValueKind == JsonValueKind.String)
            {
                var value = element.GetString();
                return string.IsNullOrWhiteSpace(value)
                    ? null
                    : new ExternalContactCandidate { Value = value.Trim(), Label = fallbackLabel };
            }
            if (element.ValueKind != JsonValueKind.Object ||
                !element.TryGetProperty(nameof(ExternalContactCandidate.Value), out var valueProperty))
            {
                return null;
            }

            var valueText = valueProperty.GetString();
            if (string.IsNullOrWhiteSpace(valueText))
            {
                return null;
            }
            var label = element.TryGetProperty(nameof(ExternalContactCandidate.Label), out var labelProperty)
                ? labelProperty.GetString()
                : null;
            return new ExternalContactCandidate
            {
                Value = valueText.Trim(),
                Label = string.IsNullOrWhiteSpace(label) ? fallbackLabel : label.Trim()
            };
        }

        private static ExternalContactCandidate Normalize(ExternalContactCandidate candidate, string fallbackLabel) => new()
        {
            Value = candidate.Value.Trim(),
            Label = string.IsNullOrWhiteSpace(candidate.Label) ? fallbackLabel : candidate.Label.Trim()
        };
    }
}
