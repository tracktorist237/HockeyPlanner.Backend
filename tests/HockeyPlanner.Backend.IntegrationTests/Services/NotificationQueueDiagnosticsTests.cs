using System.Net;
using System.Net.Http.Json;
using HockeyPlanner.Backend.Core.Entities;
using HockeyPlanner.Backend.Core.Enums;
using HockeyPlanner.Backend.Infrastructure.Data;
using HockeyPlanner.Backend.IntegrationTests.Fixtures;
using HockeyPlanner.Backend.IntegrationTests.Infrastructure;
using HockeyPlanner.Backend.WebAPI.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace HockeyPlanner.Backend.IntegrationTests.Services;

[Collection(IntegrationTestCollection.Name)]
public sealed class NotificationQueueDiagnosticsTests(HockeyPlannerWebApplicationFactory factory)
{
    [Theory]
    [InlineData(false, false, HttpStatusCode.Unauthorized)]
    [InlineData(true, false, HttpStatusCode.Forbidden)]
    [InlineData(true, true, HttpStatusCode.OK)]
    public async Task Diagnostics_RequireSuperAdmin(bool authenticated, bool admin, HttpStatusCode expected)
    {
        var token = TestContext.Current.CancellationToken;
        var user = new User { FirstName = "Queue", LastName = "Observer", AppRole = admin ? AppRole.SuperAdmin : AppRole.User };
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.Users.Add(user);
            await db.SaveChangesAsync(token);
        }
        using var client = authenticated ? AuthenticatedTestClientFactory.Create(factory, user) : factory.CreateClient();
        using var response = await client.GetAsync("/api/admin/notification-jobs/summary", token);
        Assert.Equal(expected, response.StatusCode);
        if (admin)
        {
            var text = await response.Content.ReadAsStringAsync(token);
            Assert.DoesNotContain("protectedPayload", text);
            Assert.DoesNotContain("userId", text);
            Assert.DoesNotContain("endpoint", text);
            Assert.NotNull(await response.Content.ReadFromJsonAsync<NotificationQueueSummary>(token));
        }
    }

    [Fact]
    public async Task Diagnostics_CountPendingRetryingFailedAndAgeWithoutPayload()
    {
        var token = TestContext.Current.CancellationToken;
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var now = new DateTimeOffset(2026, 10, 1, 0, 0, 0, TimeSpan.Zero);
        var diagnostics = new NotificationQueueDiagnostics(db, new FixedTimeProvider(now), Options.Create(new NotificationWorkerOptions()));
        var before = await diagnostics.ReadAsync(token);
        var oldest = new DateTime(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var jobs = new[]
        {
            new NotificationJob { Kind = NotificationJobKind.EmailConfirmation, CreatedAt = oldest, NextAttemptAt = now.UtcDateTime },
            new NotificationJob { Kind = NotificationJobKind.PasswordReset, AttemptCount = 1, CreatedAt = oldest, NextAttemptAt = now.UtcDateTime },
            new NotificationJob { Kind = NotificationJobKind.Push, Status = NotificationJobStatus.Failed, AttemptCount = 5, CompletedAt = now.AddYears(10).UtcDateTime, LastErrorCode = "provider_rejected" }
        };
        db.NotificationJobs.AddRange(jobs);
        await db.SaveChangesAsync(token);
        try
        {
            var after = await diagnostics.ReadAsync(token);
            Assert.Equal(before.Pending + 2, after.Pending);
            Assert.Equal(before.Retrying + 1, after.Retrying);
            Assert.Equal(before.TerminalFailures + 1, after.TerminalFailures);
            Assert.Equal((now.UtcDateTime - oldest).TotalSeconds, after.OldestPendingAgeSeconds);
            Assert.Contains(after.RecentFailures, value => value.JobId == jobs[2].Id && value.AttemptCount == 5 && value.ErrorCode == "provider_rejected");
            Assert.True(after.RecentFailures.Count <= 20);
        }
        finally
        {
            var ids = jobs.Select(value => value.Id).ToArray();
            await db.NotificationJobs.Where(value => ids.Contains(value.Id)).ExecuteDeleteAsync(token);
        }
    }
}
