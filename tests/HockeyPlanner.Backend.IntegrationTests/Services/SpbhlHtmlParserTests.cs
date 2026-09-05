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
        Assert.Null(match.ArenaAddress);
        Assert.Equal(Guid.Parse("534c6c6b-a6d3-43f4-896d-e4520d23e954"), match.ArenaId);
        Assert.Equal("Летнее Первенство 2026", match.TournamentName);
        Assert.Equal("Любитель", match.DivisionName);
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
    public void ParseMatch_Real118731Fixture_ReturnsFinalScoreAndSourceMetadata()
    {
        var details = new SpbhlMatchHtmlParser().ParseMatch(ReadFixture("match-118731.html"), 6590, 118731);

        Assert.NotNull(details);
        Assert.Equal(6590, details.TournamentId);
        Assert.Equal(118731, details.MatchId);
        Assert.Equal("Северная Столица", details.HomeTeamName);
        Assert.Equal("Феникс 2", details.AwayTeamName);
        Assert.Equal(3, details.HomeScore);
        Assert.Equal(2, details.AwayScore);
        Assert.Equal(SpbhlMatchStatus.Finished, details.Status);
        Assert.Equal("Многофункциональный комплекс «Север Парк Арена» Арена Север", details.ArenaName);
        Assert.Equal("Санкт-Петербург, ул. Руставели, д. 38, к. 1", details.ArenaAddress);
        Assert.Equal("Кубок SPORTKAPPA 2026", details.TournamentName);
        Assert.Equal("Д5С", details.DivisionName);
        Assert.Equal("https://spbhl.ru/Match?TournamentID=6590&MatchID=118731", details.MatchUrl);
    }

    [Fact]
    public void ParseTeamProfile_RealFixture_ReturnsOfficialMetadataWithoutInventedCoverOrDivision()
    {
        var profile = new SpbhlTeamProfileHtmlParser().ParseTeamProfile(ReadFixture("team-profile.html"), LadogaTeamId);

        Assert.NotNull(profile);
        Assert.Equal(LadogaTeamId, profile.TeamId);
        Assert.Equal("ХК \"Ладога\"", profile.Name);
        Assert.Equal("п. Сосново", profile.City);
        Assert.Equal("Россия", profile.Country);
        Assert.Equal("https://spbhl.ru/Team?TeamID=8d7c1823-0e26-4c7c-bbcb-9ab84b2fc953", profile.ProfileUrl);
        Assert.Equal("https://spbhl.ru/ImageHandler.ashx?ID=8d7c1823-0e26-4c7c-bbcb-9ab84b2fc953&Size=L&TableName=Team", profile.LogoUrl);
        Assert.Null(profile.DivisionName);
        Assert.Null(profile.CoverUrl);
        Assert.Null(profile.FoundedYear);
        Assert.Equal("Трошнев Александр Валерьевич", profile.CoachName);
        Assert.Equal("Трошнев Александр Валерьевич", profile.AdministratorName);
        Assert.Equal(["8 (921) 965-11-97"], profile.Phones);
        Assert.Empty(profile.WebsiteUrls);
    }

    [Fact]
    public void ParseTeamProfile_PhotoSection_ReturnsDistinctNormalizedCoverUrl()
    {
        var teamId = Guid.Parse("e883398e-311c-4214-8bb4-6869db4b3791");

        var profile = new SpbhlTeamProfileHtmlParser().ParseTeamProfile(
            ReadFixture("team-profile-photo.html"),
            teamId);

        Assert.NotNull(profile);
        Assert.Equal("https://spbhl.ru/ImageHandlerInt.ashx?ID=5514&Size=O&TableName=TeamSeason", profile.CoverUrl);
        Assert.NotEqual(profile.LogoUrl, profile.CoverUrl);
    }

    [Fact]
    public void ParseTeamProfile_AdministratorFixture_ParsesAdministrativeContact()
    {
        var teamId = Guid.Parse("f4286850-d18e-4e16-bbe2-a0577764a0c6");

        var profile = new SpbhlTeamProfileHtmlParser().ParseTeamProfile(
            ReadFixture("team-profile-administrator.html"),
            teamId);

        Assert.NotNull(profile);
        Assert.Equal("Пешкин Андрей Геннадьевич", profile.AdministratorName);
        Assert.Equal(["+7 (921) 409-79-39"], profile.Phones);
        Assert.Empty(profile.WebsiteUrls);
    }

    [Fact]
    public void ParseTeamProfile_LabelValueMetadata_NormalizesSupportedContacts()
    {
        var html = $$"""
            <div class="callout secondary">
              <img src="ImageHandler.ashx?ID={{LadogaTeamId}}&amp;TableName=Team" />
              <h3>Команда</h3>
              <table>
                <tr><td>Год создания:</td><td>2015</td></tr>
                <tr><td>Тренер</td><td></td></tr>
                <tr><td>Администратор</td><td>Тищенко  Артем Максимович</td></tr>
                <tr><td>Контакты</td><td>8(911)139-02-69; +7 921 111 22 33; пишите администратору</td></tr>
                <tr><td>Веб</td><td><a href="https://club.example/path">Сайт</a> club-two.example</td></tr>
              </table>
            </div>
            """;

        var profile = new SpbhlTeamProfileHtmlParser().ParseTeamProfile(html, LadogaTeamId);

        Assert.NotNull(profile);
        Assert.Equal(2015, profile.FoundedYear);
        Assert.Null(profile.CoachName);
        Assert.Equal("Тищенко Артем Максимович", profile.AdministratorName);
        Assert.Equal(["8 (911) 139-02-69", "+7 (921) 111-22-33"], profile.Phones);
        Assert.Contains("https://club.example/path", profile.WebsiteUrls);
        Assert.Contains("https://club-two.example", profile.WebsiteUrls);
        Assert.DoesNotContain(profile.WebsiteUrls, value => value.Contains("администратору", StringComparison.Ordinal));
    }

    [Fact]
    public void ParseTeamProfile_MalformedContacts_DoNotInventPhoneOrWebsite()
    {
        var html = $$"""
            <div class="callout secondary">
              <img src="ImageHandler.ashx?ID={{LadogaTeamId}}&amp;TableName=Team" />
              <h3>Команда</h3>
              <table>
                <tr><td>Год создания</td><td>неизвестно</td></tr>
                <tr><td>Контакты</td><td>пишите администратору</td></tr>
                <tr><td>Веб</td><td>официальная страница команды</td></tr>
              </table>
            </div>
            """;

        var profile = new SpbhlTeamProfileHtmlParser().ParseTeamProfile(html, LadogaTeamId);

        Assert.NotNull(profile);
        Assert.Null(profile.FoundedYear);
        Assert.Empty(profile.Phones);
        Assert.Empty(profile.WebsiteUrls);
    }

    [Fact]
    public void ParseSchedule_ExplicitRescheduledStatus_IsProviderSpecificAndConservative()
    {
        var match = Assert.Single(new SpbhlScheduleHtmlParser().ParseSchedule(ReadFixture("schedule-rescheduled.html")));

        Assert.Equal(SpbhlMatchStatus.Rescheduled, match.Status);
        Assert.Equal("Перенесён", match.RawStatus);
        Assert.Equal("Ледовый комплекс «АСК-С»", match.ArenaName);
        Assert.Equal("Санкт-Петербург, Стрельна, Фронтовая ул., 3", match.ArenaAddress);
    }

    [Fact]
    public void ParseSchedule_GlobalDescriptionOutsideArenaContainer_IsIgnored()
    {
        var html = CreateScheduleRow(
            "Match.aspx?TournamentID=6537&MatchID=118101",
            "Вт 14.07.2026",
            "19:45",
            "unknown") + "<span class=\"description\">Не адрес арены</span>";

        var match = Assert.Single(new SpbhlScheduleHtmlParser().ParseSchedule(html));

        Assert.Null(match.ArenaAddress);
    }

    [Theory]
    [InlineData(0, 118731)]
    [InlineData(6590, 0)]
    public void ParseMatch_InvalidIdentity_ReturnsNull(int tournamentId, int matchId)
    {
        Assert.Null(new SpbhlMatchHtmlParser().ParseMatch(ReadFixture("match-118731.html"), tournamentId, matchId));
    }

    [Fact]
    public void ParseMatch_MalformedMarkup_ReturnsNull()
    {
        Assert.Null(new SpbhlMatchHtmlParser().ParseMatch("<div>incomplete</div>", 6590, 118731));
    }

    [Fact]
    public void ParseTeamProfile_MalformedMarkup_ReturnsNull()
    {
        Assert.Null(new SpbhlTeamProfileHtmlParser().ParseTeamProfile("<div>incomplete</div>", LadogaTeamId));
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
        Assert.Null(new SpbhlMatchHtmlParser().ParseMatch(html, 6590, 118731));
        Assert.Null(new SpbhlTeamProfileHtmlParser().ParseTeamProfile(html, LadogaTeamId));
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
