using System.Data.Common;
using System.Net;
using System.Net.Http.Json;
using HockeyPlanner.Backend.Core.Entities;
using HockeyPlanner.Backend.Core.Enums;
using HockeyPlanner.Backend.Infrastructure.Data;
using HockeyPlanner.Backend.IntegrationTests.Fixtures;
using HockeyPlanner.Backend.IntegrationTests.Infrastructure;
using HockeyPlanner.Backend.Shared.Models.Teams;
using HockeyPlanner.Backend.WebAPI.Services;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace HockeyPlanner.Backend.IntegrationTests.Security;

[Collection(IntegrationTestCollection.Name)]
[Trait("Category", "HP82")]
public sealed class TeamMediaSnapshotRaceTests(HockeyPlannerWebApplicationFactory factory)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData("avatar")]
    [InlineData("cover")]
    public async Task Upload_ReplacedAdminMembershipDuringStorage_ReusesAuthorizedSnapshot(string kind)
    {
        var s = await TeamApiBaselineScenarioBuilder.CreateAsync(factory.Services);
        Guid originalMembershipId;
        await using (var setup = factory.Services.CreateAsyncScope())
        {
            var db = setup.ServiceProvider.GetRequiredService<AppDbContext>();
            var membership = await db.TeamMemberships.SingleAsync(x => x.TeamId == s.Pair.TeamB.Id && x.UserId == s.Admin.Id, Ct);
            originalMembershipId = membership.Id;
            membership.BadgeTitle = "Original snapshot";
            await db.SaveChangesAsync(Ct);
        }
        var storage = new GatedStorage($"https://test.invalid/hp82-race-{kind}.png");
        var reads = new MembershipReadCounter();
        using var host = factory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<IFileStorageService>();
            services.AddSingleton<IFileStorageService>(storage);
            services.AddDbContext<AppDbContext>(options => options.AddInterceptors(reads));
        }));
        using var client = host.CreateClient();
        using var signed = AuthenticatedTestClientFactory.Create(factory, s.Admin);
        client.DefaultRequestHeaders.Authorization = signed.DefaultRequestHeaders.Authorization;
        using var content = TeamNewsMediaHttpTests.Image();
        var pending = client.PostAsync($"/api/teams/{s.Pair.TeamB.Id}/{kind}/upload", content, Ct);
        Guid replacementMembershipId;
        try
        {
            // This signal proves the request has authorized/loaded its snapshot and is awaiting storage.
            await storage.Entered.WaitAsync(TimeSpan.FromSeconds(20), Ct);
            await using var concurrent = factory.Services.CreateAsyncScope();
            var db = concurrent.ServiceProvider.GetRequiredService<AppDbContext>();
            var original = await db.TeamMemberships.SingleAsync(x => x.TeamId == s.Pair.TeamB.Id && x.UserId == s.Admin.Id, Ct);
            Assert.Equal(originalMembershipId, original.Id);
            db.TeamMemberships.Remove(original);
            await db.SaveChangesAsync(Ct);
            var replacement = new TeamMembership
            {
                TeamId = s.Pair.TeamB.Id, UserId = s.Admin.Id, Role = TeamMemberRole.Admin,
                BadgeTitle = "Replacement membership"
            };
            replacementMembershipId = replacement.Id;
            Assert.NotEqual(originalMembershipId, replacementMembershipId);
            db.TeamMemberships.Add(replacement);
            await db.SaveChangesAsync(Ct);
        }
        finally
        {
            storage.Release();
        }
        using var response = await pending.WaitAsync(TimeSpan.FromSeconds(20), Ct);
        await using var verify = factory.Services.CreateAsyncScope();
        var persisted = verify.ServiceProvider.GetRequiredService<AppDbContext>();
        var team = await persisted.Teams.AsNoTracking().SingleAsync(x => x.Id == s.Pair.TeamB.Id, Ct);
        // Verify the committed state before the status assertion, exposing false-failure responses on the reviewed head.
        Assert.Equal(storage.PublicUrl, kind == "avatar" ? team.AvatarUrl : team.CoverImageUrl);
        Assert.Null(kind == "avatar" ? team.CoverImageUrl : team.AvatarUrl);
        var current = Assert.Single(await persisted.TeamMemberships.AsNoTracking()
            .Where(x => x.TeamId == team.Id && x.UserId == s.Admin.Id).ToListAsync(Ct));
        Assert.Equal(replacementMembershipId, current.Id);
        Assert.Equal(TeamMemberRole.Admin, current.Role);
        Assert.Equal("Replacement membership", current.BadgeTitle);
        Assert.Equal(3, await persisted.TeamMemberships.CountAsync(x => x.TeamId == team.Id, Ct));
        Assert.Equal(1, storage.Calls);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var dto = (await response.Content.ReadFromJsonAsync<TeamDto>(Ct))!;
        Assert.Equal(storage.PublicUrl, kind == "avatar" ? dto.AvatarUrl : dto.CoverImageUrl);
        Assert.Equal(TeamMemberRole.Admin, dto.MyRole);
        Assert.Equal("Original snapshot", dto.MyBadgeTitle);
        Assert.Equal(3, dto.MembersCount);
        Assert.Equal(1, reads.Count); // No second membership tracking/fixup query after storage.
    }

    private sealed class GatedStorage(string publicUrl) : IFileStorageService
    {
        private readonly TaskCompletionSource _entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task Entered => _entered.Task;
        public string PublicUrl => publicUrl;
        public int Calls;
        public void Release() => _release.TrySetResult();

        public async Task<FileStorageUploadResult> UploadAsync(FileStorageUploadRequest request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref Calls);
            _entered.TrySetResult();
            await _release.Task.WaitAsync(TimeSpan.FromSeconds(20), cancellationToken);
            return new FileStorageUploadResult { PublicUrl = PublicUrl, Key = "test/snapshot-race" };
        }

        public Task DeleteAsync(string key, CancellationToken cancellationToken) => throw new InvalidOperationException("No cleanup in this fix");
    }

    private sealed class MembershipReadCounter : DbCommandInterceptor
    {
        public int Count;
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData eventData,
            InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            if (command.CommandText.StartsWith("SELECT", StringComparison.Ordinal) && command.CommandText.Contains("team_memberships", StringComparison.Ordinal))
                Interlocked.Increment(ref Count);
            return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
        }
    }
}
