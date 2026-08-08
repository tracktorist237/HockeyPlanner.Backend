using System.Net;
using System.Net.Http.Json;
using HockeyPlanner.Backend.Core.Enums;
using HockeyPlanner.Backend.IntegrationTests.Fixtures;
using HockeyPlanner.Backend.IntegrationTests.Infrastructure;
using HockeyPlanner.Backend.Infrastructure.Data;
using HockeyPlanner.Backend.Shared.Models.Events;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace HockeyPlanner.Backend.IntegrationTests.Security;

[Collection(IntegrationTestCollection.Name)]
public sealed class AttendanceAndPlayerSecurityExpectationTests
{
    private readonly HockeyPlannerWebApplicationFactory _application;

    public AttendanceAndPlayerSecurityExpectationTests(HockeyPlannerWebApplicationFactory application)
    {
        _application = application;
    }

    [Fact]
    [Trait("Category", "SecurityExpectation")]
    public async Task SpoofedCurrentUserId_DoesNotOverrideJwtActor_OrChangeForeignAttendance()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var scenario = await TwoTeamSecurityScenarioBuilder.CreateAsync(
            _application.Services,
            cancellationToken);
        using var client = AuthenticatedTestClientFactory.Create(_application, scenario.UserA);
        var update = new UpdateAttendanceRequest
        {
            Status = AttendanceStatus.Declined,
            Notes = "Unauthorized attendance update",
        };

        using var response = await client.PostAsJsonAsync(
            $"/api/events/{scenario.EventB.Id}/attendance/{scenario.UserB.Id}" +
            $"?currentUserId={scenario.UserB.Id}",
            update,
            cancellationToken);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);

        await using var scope = _application.Services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var attendance = await dbContext.Attendances
            .AsNoTracking()
            .SingleAsync(value => value.Id == scenario.AttendanceB.Id, cancellationToken);
        Assert.Equal(AttendanceStatus.Confirmed, attendance.Status);
        Assert.Null(attendance.Notes);
    }

    [Fact]
    [Trait("Category", "SecurityExpectation")]
    public async Task DeleteForeignPlayer_IsForbidden_AndPlayerRemainsInRoster()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var scenario = await TwoTeamSecurityScenarioBuilder.CreateAsync(
            _application.Services,
            cancellationToken);
        using var client = AuthenticatedTestClientFactory.Create(_application, scenario.UserA);

        using var response = await client.DeleteAsync(
            $"/api/players?playerId={scenario.PlayerB.Id}",
            cancellationToken);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);

        await using var scope = _application.Services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.True(await dbContext.Players
            .AsNoTracking()
            .AnyAsync(value => value.Id == scenario.PlayerB.Id, cancellationToken));
    }
}
