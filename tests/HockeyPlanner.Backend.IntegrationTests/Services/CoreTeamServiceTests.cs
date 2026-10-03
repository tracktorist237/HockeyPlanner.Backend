using HockeyPlanner.Backend.Application.Abstractions.Services;
using HockeyPlanner.Backend.Core.Enums;
using HockeyPlanner.Backend.Infrastructure.Data;
using HockeyPlanner.Backend.IntegrationTests.Fixtures;
using HockeyPlanner.Backend.IntegrationTests.Infrastructure;
using HockeyPlanner.Backend.Shared.Models.Teams;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace HockeyPlanner.Backend.IntegrationTests.Services;

[Collection(IntegrationTestCollection.Name)]
[Trait("Category", "HP81")]
public sealed class CoreTeamServiceTests(HockeyPlannerWebApplicationFactory factory)
{
    [Fact]
    public async Task ApplicationUseCase_AtomicallyCreatesOwner_UsingTimeProvider()
    {
        var ct = TestContext.Current.CancellationToken;
        var seed = await TwoTeamSecurityScenarioBuilder.CreateAsync(factory.Services, ct);
        var instant = new DateTimeOffset(2031, 2, 3, 4, 5, 6, TimeSpan.Zero);
        using var host = factory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<TimeProvider>();
            services.AddSingleton<TimeProvider>(new FixedTimeProvider(instant));
        }));
        Guid teamId;
        await using (var scope = host.Services.CreateAsyncScope())
        {
            var service = scope.ServiceProvider.GetRequiredService<ICoreTeamService>();
            var dto = await service.CreateTeam(seed.UserA.Id, new CreateTeamRequest { Name = $"Service {Guid.NewGuid():N}" }, ct);
            teamId = dto.Id;
            Assert.Equal(TeamMemberRole.Owner, dto.MyRole);
            Assert.Equal(1, dto.MembersCount);
        }
        await using var verify = factory.Services.CreateAsyncScope();
        var db = verify.ServiceProvider.GetRequiredService<AppDbContext>();
        var team = await db.Teams.AsNoTracking().SingleAsync(x => x.Id == teamId, ct);
        var owner = await db.TeamMemberships.AsNoTracking().SingleAsync(x => x.TeamId == teamId, ct);
        Assert.Equal(seed.UserA.Id, owner.UserId);
        Assert.Equal(TeamMemberRole.Owner, owner.Role);
        Assert.Equal(instant.UtcDateTime, team.CreatedAt);
        Assert.Equal(instant.UtcDateTime, owner.CreatedAt);
        Assert.Equal(instant.UtcDateTime, owner.UpdatedAt);
    }

    [Fact]
    public async Task EveryUseCase_PropagatesCancellationToPostgres()
    {
        var ct = TestContext.Current.CancellationToken;
        var seed = await TeamApiBaselineScenarioBuilder.CreateAsync(factory.Services, publicB: true);
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        var token = cancelled.Token;
        var id = seed.Pair.TeamB.Id;
        var actor = seed.Pair.UserB.Id;
        Func<ICoreTeamService, Task>[] calls =
        [
            s => s.GetMyTeams(actor, token),
            s => s.GetPublicTeams(token),
            s => s.GetTeam(id, true, actor, token),
            s => s.GetTeamMembers(id, true, actor, token),
            s => s.CreateTeam(actor, new CreateTeamRequest { Name = "Cancelled team" }, token),
            s => s.UpdateTeam(id, actor, new UpdateTeamRequest { Name = "Cancelled update" }, token),
            s => s.UpdateTeamMember(id, seed.Member.Id, actor, new UpdateTeamMemberRequest(), token),
            s => s.RemoveTeamMember(id, seed.Member.Id, actor, token),
            s => s.JoinByCode(actor, new JoinTeamByCodeRequest { Code = seed.Pair.TeamB.InviteCode }, token),
            s => s.JoinPublic(id, actor, null, token),
            s => s.LeaveTeam(id, actor, token),
            s => s.UpdateMyTeamJerseyNumber(id, actor, new UpdateMyTeamJerseyNumberRequest(), token)
        ];
        foreach (var call in calls)
        {
            await using var scope = factory.Services.CreateAsyncScope();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => call(scope.ServiceProvider.GetRequiredService<ICoreTeamService>()));
        }
        await using var verify = factory.Services.CreateAsyncScope();
        var db = verify.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.False(await db.Teams.AnyAsync(x => x.Name == "Cancelled team" || x.Name == "Cancelled update", ct));
        Assert.Equal(3, await db.TeamMemberships.CountAsync(x => x.TeamId == id, ct));
        Assert.Equal(TeamMemberRole.Owner, (await db.TeamMemberships.SingleAsync(x => x.TeamId == id && x.UserId == actor, ct)).Role);
    }
}
