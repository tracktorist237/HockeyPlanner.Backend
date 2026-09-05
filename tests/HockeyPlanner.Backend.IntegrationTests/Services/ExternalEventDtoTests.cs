using System.Net.Http.Json;
using HockeyPlanner.Backend.Core.Entities;
using HockeyPlanner.Backend.Core.Enums;
using HockeyPlanner.Backend.Infrastructure.Data;
using HockeyPlanner.Backend.IntegrationTests.Infrastructure;
using HockeyPlanner.Backend.Shared.Models.Events;
using Microsoft.Extensions.DependencyInjection;

namespace HockeyPlanner.Backend.IntegrationTests.Services;

[Collection(IntegrationTestCollection.Name)]
[Trait("Category", "ExternalLeagueServices")]
public sealed class ExternalEventDtoTests(HockeyPlannerWebApplicationFactory factory)
{
    [Fact]
    public async Task ListAndDetails_ExposeExternalLeagueFields()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var scope = factory.Services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var team = new Team
        {
            Name = $"External DTO {Guid.NewGuid():N}",
            InviteCode = Guid.NewGuid().ToString("N")[..20],
            Visibility = TeamVisibility.Public
        };
        var scheduledEvent = new ScheduledEvent
        {
            Team = team,
            Title = "Home — Away",
            Type = EventType.Game,
            StartTime = DateTime.UtcNow.AddDays(1),
            DurationMinutes = 75,
            Status = EventStatus.Completed,
            LocationName = "Ледовый комплекс «АСК-С»",
            LocationAddress = "Санкт-Петербург, Стрельна, Фронтовая ул., 3",
            HomeTeamName = "Home",
            AwayTeamName = "Away",
            ExternalLeagueProvider = ExternalLeagueProvider.Spbhl,
            ExternalDivisionName = "Любитель 3",
            ExternalTournamentName = "Кубок",
            SpbhlTournamentId = 6590,
            SpbhlMatchId = 118731,
            SpbhlMatchUrl = "https://spbhl.ru/Match?TournamentID=6590&MatchID=118731",
            HomeScore = 4,
            AwayScore = 2,
            CreatedAt = DateTime.UtcNow
        };
        context.Add(scheduledEvent);
        await context.SaveChangesAsync(cancellationToken);

        var list = await factory.Client.GetFromJsonAsync<EventListDto>(
            $"/api/events?teamId={team.Id}", cancellationToken);
        var details = await factory.Client.GetFromJsonAsync<EventDto>(
            $"/api/events/{scheduledEvent.Id}", cancellationToken);

        var listItem = Assert.Single(list!.Events!);
        AssertExternalFields(listItem.ExternalLeagueProvider, listItem.ExternalDivisionName,
            listItem.ExternalTournamentName, listItem.SpbhlTournamentId, listItem.SpbhlMatchId,
            listItem.SpbhlMatchUrl, listItem.HomeScore, listItem.AwayScore);
        Assert.Equal("Home", listItem.HomeTeamName);
        Assert.Equal("Away", listItem.AwayTeamName);
        AssertExternalFields(details!.ExternalLeagueProvider, details.ExternalDivisionName,
            details.ExternalTournamentName, details.SpbhlTournamentId, details.SpbhlMatchId,
            details.SpbhlMatchUrl, details.HomeScore, details.AwayScore);
    }

    private static void AssertExternalFields(
        ExternalLeagueProvider? provider,
        string? division,
        string? tournament,
        int? tournamentId,
        int? matchId,
        string? matchUrl,
        int? homeScore,
        int? awayScore)
    {
        Assert.Equal(ExternalLeagueProvider.Spbhl, provider);
        Assert.Equal("Любитель 3", division);
        Assert.Equal("Кубок", tournament);
        Assert.Equal(6590, tournamentId);
        Assert.Equal(118731, matchId);
        Assert.Equal("https://spbhl.ru/Match?TournamentID=6590&MatchID=118731", matchUrl);
        Assert.Equal(4, homeScore);
        Assert.Equal(2, awayScore);
    }
}
