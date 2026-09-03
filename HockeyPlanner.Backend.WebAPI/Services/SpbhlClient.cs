using HockeyPlanner.Backend.WebAPI.Models.Spbhl;

namespace HockeyPlanner.Backend.WebAPI.Services
{
    public class SpbhlClient : ISpbhlClient
    {
        private readonly HttpClient _httpClient;
        private readonly ISpbhlTeamHtmlParser _teamHtmlParser;
        private readonly ISpbhlScheduleHtmlParser _scheduleHtmlParser;

        public SpbhlClient(
            HttpClient httpClient,
            ISpbhlTeamHtmlParser teamHtmlParser,
            ISpbhlScheduleHtmlParser scheduleHtmlParser)
        {
            _httpClient = httpClient;
            _teamHtmlParser = teamHtmlParser;
            _scheduleHtmlParser = scheduleHtmlParser;
        }

        public async Task<IReadOnlyCollection<SpbhlTeamSearchItem>> SearchTeamsAsync(
            string? title,
            CancellationToken cancellationToken)
        {
            var path = "Teams";
            if (!string.IsNullOrWhiteSpace(title))
            {
                path += $"?Title={Uri.EscapeDataString(title.Trim())}";
            }

            var html = await GetHtmlAsync(path, cancellationToken);
            return _teamHtmlParser.ParseTeams(html);
        }

        public async Task<IReadOnlyCollection<SpbhlMatchItem>> GetTeamScheduleAsync(
            Guid teamId,
            CancellationToken cancellationToken)
        {
            var html = await GetHtmlAsync($"Schedule?TeamID={teamId:D}", cancellationToken);
            return _scheduleHtmlParser.ParseSchedule(html);
        }

        private async Task<string> GetHtmlAsync(string path, CancellationToken cancellationToken)
        {
            using var response = await _httpClient.GetAsync(path, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            response.EnsureSuccessStatusCode();
            return await response.Content.ReadAsStringAsync(cancellationToken);
        }
    }
}
