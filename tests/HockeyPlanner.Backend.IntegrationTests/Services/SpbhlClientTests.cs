using System.Net;
using HockeyPlanner.Backend.WebAPI.Models.Spbhl;
using HockeyPlanner.Backend.WebAPI.Services;

namespace HockeyPlanner.Backend.IntegrationTests.Services;

public class SpbhlClientTests
{
    private static readonly Guid LadogaTeamId = Guid.Parse("8d7c1823-0e26-4c7c-bbcb-9ab84b2fc953");

    [Fact]
    public async Task SearchTeamsAsync_EncodesTitle_DelegatesHtmlAndReturnsParserResult()
    {
        const string html = "<html>teams response</html>";
        var expected = new[]
        {
            new SpbhlTeamSearchItem { TeamId = LadogaTeamId, Name = "Ладога", ProfileUrl = "https://spbhl.ru/Team.aspx" }
        };
        var parser = new RecordingTeamParser(expected);
        using var handler = RecordingHttpMessageHandler.Return(HttpStatusCode.OK, html);
        using var httpClient = CreateHttpClient(handler);
        var client = CreateSpbhlClient(httpClient, parser, new RecordingScheduleParser([]));

        var result = await client.SearchTeamsAsync(" Ладога СПб ", TestContext.Current.CancellationToken);

        Assert.Same(expected, result);
        Assert.Equal(html, parser.Html);
        Assert.Equal(HttpMethod.Get, handler.Method);
        Assert.Equal("/Teams?Title=%D0%9B%D0%B0%D0%B4%D0%BE%D0%B3%D0%B0%20%D0%A1%D0%9F%D0%B1", handler.RequestUri?.PathAndQuery);
        Assert.Equal("HockeyPlanner/1.0", handler.UserAgent);
    }

    [Fact]
    public async Task SearchTeamsAsync_WithoutTitle_RequestsUnfilteredTeamsPage()
    {
        using var handler = RecordingHttpMessageHandler.Return(HttpStatusCode.OK, "<html></html>");
        using var httpClient = CreateHttpClient(handler);
        var client = CreateSpbhlClient(httpClient, new RecordingTeamParser([]), new RecordingScheduleParser([]));

        await client.SearchTeamsAsync(null, TestContext.Current.CancellationToken);

        Assert.Equal("/Teams", handler.RequestUri?.PathAndQuery);
    }

    [Fact]
    public async Task GetTeamScheduleAsync_UsesProvidedTeamId_DelegatesHtmlAndReturnsParserResult()
    {
        const string html = "<html>schedule response</html>";
        var expected = new[]
        {
            new SpbhlMatchItem
            {
                MatchId = 118664,
                TournamentId = 6537,
                HomeTeamName = "Ладога",
                AwayTeamName = "АЛГА",
                Status = SpbhlMatchStatus.Unknown,
                MatchUrl = "https://spbhl.ru/Match.aspx?TournamentID=6537&MatchID=118664"
            },
            new SpbhlMatchItem
            {
                MatchId = 118101,
                TournamentId = 6537,
                HomeTeamName = "Ладога",
                AwayTeamName = "Хоккейное Королевство",
                HomeScore = 4,
                AwayScore = 2,
                Status = SpbhlMatchStatus.Finished,
                MatchUrl = "https://spbhl.ru/Match.aspx?TournamentID=6537&MatchID=118101"
            }
        };
        var parser = new RecordingScheduleParser(expected);
        using var handler = RecordingHttpMessageHandler.Return(HttpStatusCode.OK, html);
        using var httpClient = CreateHttpClient(handler);
        var client = CreateSpbhlClient(httpClient, new RecordingTeamParser([]), parser);

        var result = await client.GetTeamScheduleAsync(LadogaTeamId, TestContext.Current.CancellationToken);

        Assert.Same(expected, result);
        Assert.Equal(html, parser.Html);
        Assert.Equal(HttpMethod.Get, handler.Method);
        Assert.Equal($"/Schedule?TeamID={LadogaTeamId:D}", handler.RequestUri?.PathAndQuery);
        Assert.DoesNotContain("SeasonID", handler.RequestUri?.Query, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task GetMatchDetailsAsync_UsesIdentityAndDelegatesHtml()
    {
        const string html = "<html>match response</html>";
        var expected = new SpbhlMatchDetails
        {
            TournamentId = 6590,
            MatchId = 118731,
            HomeTeamName = "Северная Столица",
            AwayTeamName = "Феникс 2",
            HomeScore = 3,
            AwayScore = 2,
            Status = SpbhlMatchStatus.Finished
        };
        var parser = new RecordingMatchParser(expected);
        using var handler = RecordingHttpMessageHandler.Return(HttpStatusCode.OK, html);
        using var httpClient = CreateHttpClient(handler);
        var client = new SpbhlClient(
            httpClient,
            new RecordingTeamParser([]),
            new RecordingScheduleParser([]),
            parser,
            new RecordingTeamProfileParser(null));

        var result = await client.GetMatchDetailsAsync(6590, 118731, TestContext.Current.CancellationToken);

        Assert.Same(expected, result);
        Assert.Equal(html, parser.Html);
        Assert.Equal(6590, parser.TournamentId);
        Assert.Equal(118731, parser.MatchId);
        Assert.Equal("/Match?TournamentID=6590&MatchID=118731", handler.RequestUri?.PathAndQuery);
    }

    [Fact]
    public async Task GetTeamProfileAsync_UsesTeamIdAndDelegatesHtml()
    {
        const string html = "<html>team profile response</html>";
        var expected = new SpbhlTeamProfile { TeamId = LadogaTeamId, Name = "ХК \"Ладога\"" };
        var parser = new RecordingTeamProfileParser(expected);
        using var handler = RecordingHttpMessageHandler.Return(HttpStatusCode.OK, html);
        using var httpClient = CreateHttpClient(handler);
        var client = new SpbhlClient(
            httpClient,
            new RecordingTeamParser([]),
            new RecordingScheduleParser([]),
            new RecordingMatchParser(null),
            parser);

        var result = await client.GetTeamProfileAsync(LadogaTeamId, TestContext.Current.CancellationToken);

        Assert.Same(expected, result);
        Assert.Equal(html, parser.Html);
        Assert.Equal(LadogaTeamId, parser.TeamId);
        Assert.Equal($"/Team?TeamID={LadogaTeamId:D}", handler.RequestUri?.PathAndQuery);
    }

    [Theory]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.InternalServerError)]
    public async Task NonSuccessStatus_ThrowsHttpRequestException(HttpStatusCode statusCode)
    {
        using var handler = RecordingHttpMessageHandler.Return(statusCode, "upstream error");
        using var httpClient = CreateHttpClient(handler);
        var client = CreateSpbhlClient(httpClient, new RecordingTeamParser([]), new RecordingScheduleParser([]));

        var exception = await Assert.ThrowsAsync<HttpRequestException>(() =>
            client.GetTeamScheduleAsync(LadogaTeamId, TestContext.Current.CancellationToken));

        Assert.Equal(statusCode, exception.StatusCode);
    }

    [Fact]
    public async Task Cancellation_IsPropagatedToHttpHandler()
    {
        var requestStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationToken observedToken = default;
        using var handler = new RecordingHttpMessageHandler(async (_, cancellationToken) =>
        {
            observedToken = cancellationToken;
            requestStarted.SetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK);
        });
        using var httpClient = CreateHttpClient(handler);
        var client = CreateSpbhlClient(httpClient, new RecordingTeamParser([]), new RecordingScheduleParser([]));
        using var cancellation = new CancellationTokenSource();

        var request = client.GetTeamScheduleAsync(LadogaTeamId, cancellation.Token);
        await requestStarted.Task.WaitAsync(TestContext.Current.CancellationToken);
        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => request);
        Assert.True(observedToken.IsCancellationRequested);
    }

    [Fact]
    public async Task MalformedHtmlWithSuccessStatus_FollowsParserEmptyResultContract()
    {
        using var handler = RecordingHttpMessageHandler.Return(HttpStatusCode.OK, "<table><tr><td>incomplete");
        using var httpClient = CreateHttpClient(handler);
        var client = CreateSpbhlClient(httpClient, new SpbhlTeamHtmlParser(), new SpbhlScheduleHtmlParser());

        var result = await client.GetTeamScheduleAsync(LadogaTeamId, TestContext.Current.CancellationToken);

        Assert.Empty(result);
    }

    [Fact]
    public async Task RealOfflineFixtures_AreParsedThroughHttpClientTransport()
    {
        var fixture = ReadFixture("schedule-future.html") + ReadFixture("schedule-finished.html");
        using var handler = RecordingHttpMessageHandler.Return(HttpStatusCode.OK, fixture);
        using var httpClient = CreateHttpClient(handler);
        var client = CreateSpbhlClient(httpClient, new SpbhlTeamHtmlParser(), new SpbhlScheduleHtmlParser());

        var result = await client.GetTeamScheduleAsync(LadogaTeamId, TestContext.Current.CancellationToken);

        Assert.Contains(result, match => match.MatchId == 118664 && match.HomeScore is null);
        Assert.Contains(result, match => match.MatchId == 118101 && match.HomeScore == 4 && match.AwayScore == 2);
    }

    private static HttpClient CreateHttpClient(HttpMessageHandler handler)
    {
        var client = new HttpClient(handler)
        {
            BaseAddress = new Uri("https://spbhl.ru/"),
            Timeout = TimeSpan.FromSeconds(15)
        };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("HockeyPlanner/1.0");
        return client;
    }

    private static SpbhlClient CreateSpbhlClient(
        HttpClient httpClient,
        ISpbhlTeamHtmlParser teamParser,
        ISpbhlScheduleHtmlParser scheduleParser)
    {
        return new SpbhlClient(
            httpClient,
            teamParser,
            scheduleParser,
            new RecordingMatchParser(null),
            new RecordingTeamProfileParser(null));
    }

    private static string ReadFixture(string fileName)
    {
        return File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "Spbhl", fileName));
    }

    private sealed class RecordingTeamParser : ISpbhlTeamHtmlParser
    {
        private readonly IReadOnlyCollection<SpbhlTeamSearchItem> _result;

        public RecordingTeamParser(IReadOnlyCollection<SpbhlTeamSearchItem> result)
        {
            _result = result;
        }

        public string? Html { get; private set; }

        public IReadOnlyCollection<SpbhlTeamSearchItem> ParseTeams(string html)
        {
            Html = html;
            return _result;
        }
    }

    private sealed class RecordingScheduleParser : ISpbhlScheduleHtmlParser
    {
        private readonly IReadOnlyCollection<SpbhlMatchItem> _result;

        public RecordingScheduleParser(IReadOnlyCollection<SpbhlMatchItem> result)
        {
            _result = result;
        }

        public string? Html { get; private set; }

        public IReadOnlyCollection<SpbhlMatchItem> ParseSchedule(string html)
        {
            Html = html;
            return _result;
        }
    }

    private sealed class RecordingMatchParser(SpbhlMatchDetails? result) : ISpbhlMatchHtmlParser
    {
        public string? Html { get; private set; }
        public int TournamentId { get; private set; }
        public int MatchId { get; private set; }

        public SpbhlMatchDetails? ParseMatch(string html, int tournamentId, int matchId)
        {
            Html = html;
            TournamentId = tournamentId;
            MatchId = matchId;
            return result;
        }
    }

    private sealed class RecordingTeamProfileParser(SpbhlTeamProfile? result) : ISpbhlTeamProfileHtmlParser
    {
        public string? Html { get; private set; }
        public Guid TeamId { get; private set; }

        public SpbhlTeamProfile? ParseTeamProfile(string html, Guid teamId)
        {
            Html = html;
            TeamId = teamId;
            return result;
        }
    }

    private sealed class RecordingHttpMessageHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> _responseFactory;

        public RecordingHttpMessageHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> responseFactory)
        {
            _responseFactory = responseFactory;
        }

        public HttpMethod? Method { get; private set; }
        public Uri? RequestUri { get; private set; }
        public string? UserAgent { get; private set; }

        public static RecordingHttpMessageHandler Return(HttpStatusCode statusCode, string content)
        {
            return new RecordingHttpMessageHandler((_, _) => Task.FromResult(new HttpResponseMessage(statusCode)
            {
                Content = new StringContent(content)
            }));
        }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Method = request.Method;
            RequestUri = request.RequestUri;
            UserAgent = request.Headers.UserAgent.ToString();
            return _responseFactory(request, cancellationToken);
        }
    }
}
