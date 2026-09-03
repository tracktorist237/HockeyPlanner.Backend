using HockeyPlanner.Backend.WebAPI.Models.Spbhl;
using HockeyPlanner.Backend.WebAPI.Services;

namespace HockeyPlanner.Backend.IntegrationTests.Services;

public class SpbhlHtmlParserTests
{
    private static readonly Guid LadogaTeamId = Guid.Parse("8d7c1823-0e26-4c7c-bbcb-9ab84b2fc953");
    private static readonly Guid SteelTopTeamId = Guid.Parse("67a1f9c3-ba72-41d7-a96d-0b37759e7908");

    [Fact]
    public void ParseTeams_RealFixture_ReturnsNormalizedTeams()
    {
        var parser = new SpbhlTeamHtmlParser();

        var result = parser.ParseTeams(ReadFixture("teams.html"));

        Assert.Equal(2, result.Count);

        var ladoga = Assert.Single(result, team => team.TeamId == LadogaTeamId);
        Assert.Equal("Ладога", ladoga.Name);
        Assert.Equal("п. Сосново", ladoga.City);
        Assert.Equal("Россия", ladoga.Country);
        Assert.Equal("https://spbhl.ru/Team.aspx?TeamID=8d7c1823-0e26-4c7c-bbcb-9ab84b2fc953", ladoga.ProfileUrl);
        Assert.Equal("https://spbhl.ru/ImageHandler.ashx?ID=8d7c1823-0e26-4c7c-bbcb-9ab84b2fc953&Size=M&TableName=Team", ladoga.LogoUrl);
        Assert.Equal("Дивизион Любитель 2", ladoga.DivisionName);
        Assert.Null(ladoga.TournamentId);

        var steelTop = Assert.Single(result, team => team.TeamId == SteelTopTeamId);
        Assert.Equal("Сталь TОП", steelTop.Name);
        Assert.Equal("Санкт-Петербург", steelTop.City);
    }

    [Fact]
    public void ParseTeams_DuplicateFixture_DeduplicatesByTeamId()
    {
        var parser = new SpbhlTeamHtmlParser();
        var fixture = ReadFixture("teams.html");

        var result = parser.ParseTeams(fixture + fixture);

        Assert.Equal(2, result.Count);
        Assert.Equal(2, result.Select(team => team.TeamId).Distinct().Count());
    }

    [Fact]
    public void ParseTeams_MalformedTeamId_IgnoresOnlyBrokenCard()
    {
        var html = $$"""
            <div class="callout">
              <h4><a href="Team.aspx?TeamID=not-a-guid">Broken team</a></h4>
            </div>
            <div class="callout">
              <h4><a href="Team.aspx?TeamID={{LadogaTeamId}}&amp;TournamentID=6537">Ладога</a></h4>
            </div>
            """;

        var result = new SpbhlTeamHtmlParser().ParseTeams(html);

        var team = Assert.Single(result);
        Assert.Equal(LadogaTeamId, team.TeamId);
        Assert.Equal(6537, team.TournamentId);
    }

    [Fact]
    public void ParseSchedule_FutureFixture_ReturnsIdentityAndUnknownStatus()
    {
        var parser = new SpbhlScheduleHtmlParser();

        var match = Assert.Single(parser.ParseSchedule(ReadFixture("schedule-future.html")));

        Assert.Equal(118664, match.MatchId);
        Assert.Equal(6537, match.TournamentId);
        Assert.Equal(new DateTimeOffset(2026, 9, 6, 19, 0, 0, TimeSpan.FromHours(3)), match.StartTime);
        Assert.Equal("Ладога", match.HomeTeamName);
        Assert.Equal("АЛГА", match.AwayTeamName);
        Assert.Null(match.HomeTeamId);
        Assert.Null(match.AwayTeamId);
        Assert.Equal("АХФ Арена", match.ArenaName);
        Assert.Equal(Guid.Parse("534c6c6b-a6d3-43f4-896d-e4520d23e954"), match.ArenaId);
        Assert.Null(match.HomeScore);
        Assert.Null(match.AwayScore);
        Assert.Equal(SpbhlMatchStatus.Unknown, match.Status);
        Assert.Null(match.RawStatus);
        Assert.Equal("https://spbhl.ru/Match.aspx?TournamentID=6537&MatchID=118664", match.MatchUrl);
    }

    [Fact]
    public void ParseSchedule_FinishedFixture_ReturnsScoreAndFinishedStatus()
    {
        var parser = new SpbhlScheduleHtmlParser();

        var match = Assert.Single(parser.ParseSchedule(ReadFixture("schedule-finished.html")));

        Assert.Equal(118101, match.MatchId);
        Assert.Equal(6537, match.TournamentId);
        Assert.Equal(new DateTimeOffset(2026, 7, 14, 19, 45, 0, TimeSpan.FromHours(3)), match.StartTime);
        Assert.Equal("Ладога", match.HomeTeamName);
        Assert.Equal("Хоккейное Королевство", match.AwayTeamName);
        Assert.Equal("Гранд Каньон Айс", match.ArenaName);
        Assert.Equal(Guid.Parse("495dd04d-1150-4829-b98e-0adf7dd7259f"), match.ArenaId);
        Assert.Equal(4, match.HomeScore);
        Assert.Equal(2, match.AwayScore);
        Assert.Equal(SpbhlMatchStatus.Finished, match.Status);
        Assert.Equal("Протокол матча", match.RawStatus);
    }

    [Fact]
    public void ParseSchedule_RepeatedParse_IsDeterministicAndDeduplicated()
    {
        var parser = new SpbhlScheduleHtmlParser();
        var fixture = ReadFixture("schedule-finished.html");

        var first = Assert.Single(parser.ParseSchedule(fixture));
        var second = Assert.Single(parser.ParseSchedule(fixture + fixture));

        Assert.Equal(first.MatchId, second.MatchId);
        Assert.Equal(first.TournamentId, second.TournamentId);
        Assert.Equal(first.StartTime, second.StartTime);
        Assert.Equal(first.HomeTeamName, second.HomeTeamName);
        Assert.Equal(first.AwayTeamName, second.AwayTeamName);
        Assert.Equal(first.HomeScore, second.HomeScore);
        Assert.Equal(first.AwayScore, second.AwayScore);
        Assert.Equal(first.Status, second.Status);
    }

    [Theory]
    [InlineData("Match.aspx?TournamentID=6537", "Вт 14.07.2026", "19:45")]
    [InlineData("Match.aspx?MatchID=118101", "Вт 14.07.2026", "19:45")]
    [InlineData("Match.aspx?TournamentID=6537&MatchID=118101", "date missing", "19:45")]
    public void ParseSchedule_MissingCriticalField_IgnoresRow(string matchUrl, string date, string time)
    {
        var result = new SpbhlScheduleHtmlParser().ParseSchedule(CreateScheduleRow(matchUrl, date, time, "unknown"));

        Assert.Empty(result);
    }

    [Fact]
    public void ParseSchedule_UnknownScore_KeepsMatchWithUnknownStatus()
    {
        var result = new SpbhlScheduleHtmlParser().ParseSchedule(CreateScheduleRow(
            "Match.aspx?TournamentID=6537&MatchID=118101",
            "Вт 14.07.2026",
            "19:45",
            "отложен"));

        var match = Assert.Single(result);
        Assert.Null(match.HomeScore);
        Assert.Null(match.AwayScore);
        Assert.Equal(SpbhlMatchStatus.Unknown, match.Status);
        Assert.Null(match.RawStatus);
    }

    [Fact]
    public void ParseSchedule_MissingAwayTeam_IgnoresRow()
    {
        var html = CreateScheduleRow(
            "Match.aspx?TournamentID=6537&MatchID=118101",
            "Вт 14.07.2026",
            "19:45",
            "4 : 2").Replace("Home Team - Away Team", "Home Team", StringComparison.Ordinal);

        var result = new SpbhlScheduleHtmlParser().ParseSchedule(html);

        Assert.Empty(result);
    }

    [Theory]
    [InlineData("")]
    [InlineData("<table><tr><td>incomplete")]
    public void Parsers_EmptyOrInvalidHtml_ReturnEmpty(string html)
    {
        Assert.Empty(new SpbhlTeamHtmlParser().ParseTeams(html));
        Assert.Empty(new SpbhlScheduleHtmlParser().ParseSchedule(html));
    }

    private static string ReadFixture(string fileName)
    {
        return File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "Spbhl", fileName));
    }

    private static string CreateScheduleRow(string matchUrl, string date, string time, string score)
    {
        return $$"""
            <table id="MatchGridView">
              <tr>
                <td>Tournament</td><td>1/01</td><td>2</td>
                <td>{{date}}</td><td>{{time}}</td>
                <td><a href="Arena.aspx?ArenaID=495dd04d-1150-4829-b98e-0adf7dd7259f" title="Test arena">Arena</a></td>
                <td><a href="{{matchUrl}}">Home Team - Away Team</a></td>
                <td>{{score}}</td><td></td>
              </tr>
            </table>
            """;
    }
}
