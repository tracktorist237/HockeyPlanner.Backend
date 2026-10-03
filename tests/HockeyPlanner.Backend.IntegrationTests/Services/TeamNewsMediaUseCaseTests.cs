using System.Data.Common;
using System.Net.Http.Json;
using HockeyPlanner.Backend.Application.Abstractions.Services;
using HockeyPlanner.Backend.Core.Entities;
using HockeyPlanner.Backend.Core.Enums;
using HockeyPlanner.Backend.Infrastructure.Data;
using HockeyPlanner.Backend.IntegrationTests.Fixtures;
using HockeyPlanner.Backend.IntegrationTests.Infrastructure;
using HockeyPlanner.Backend.Shared.Models.Teams;
using HockeyPlanner.Backend.WebAPI.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;
using static HockeyPlanner.Backend.IntegrationTests.Security.TeamApiBaselineTests;

namespace HockeyPlanner.Backend.IntegrationTests.Services;

[Collection(IntegrationTestCollection.Name)]
[Trait("Category", "HP82")]
public sealed class TeamNewsMediaUseCaseTests(HockeyPlannerWebApplicationFactory factory)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData("team_news")]
    [InlineData("notifications")]
    [InlineData("notification_jobs")]
    [InlineData("after-enqueue")]
    [InlineData("commit")]
    public async Task CreateHttp_FailedWriteStagingOrCommit_RollsBackNewsAndDurableIntent(string boundary)
    {
        var s = await TeamApiBaselineScenarioBuilder.CreateAsync(factory.Services);
        var sql = new FailInsert(boundary);
        var commit = new FailCommit(boundary == "commit");
        var push = new RejectPush();
        var stagedFailure = new StagedFailure();
        using var host = factory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            services.AddDbContext<AppDbContext>(options => options.AddInterceptors(sql, commit));
            services.RemoveAll<IWebPushService>();
            services.AddSingleton<IWebPushService>(push);
            if (boundary == "after-enqueue")
            {
                services.RemoveAll<INotificationService>();
                services.AddScoped<INotificationService>(p => new FailAfterEnqueue(
                    new NotificationService(p.GetRequiredService<AppDbContext>(), p.GetRequiredService<NotificationOutbox>()),
                    p.GetRequiredService<AppDbContext>(), stagedFailure));
            }
        }));
        using var client = host.CreateClient();
        using var signed = AuthenticatedTestClientFactory.Create(factory, s.Admin);
        client.DefaultRequestHeaders.Authorization = signed.DefaultRequestHeaders.Authorization;
        var title = $"Failed HP82 {Guid.NewGuid():N}";
        var problem = await Json(client.PostAsJsonAsync($"/api/teams/{s.Pair.TeamB.Id}/news",
            new { title, body = "Body", sendNotification = true }, Ct), 500);
        Assert.DoesNotContain("injected", problem.ToJsonString());
        Assert.True(sql.Failed || commit.Failed || stagedFailure.Failed); // Prove the intended fault was reached.
        Assert.Equal(boundary != "team_news", sql.NewsWritten);
        if (boundary is "commit" or "after-enqueue") Assert.True(sql.JobsWritten);
        Assert.Equal(0, push.Calls);
        await using var verify = factory.Services.CreateAsyncScope();
        var db = verify.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.False(await db.TeamNews.AsNoTracking().AnyAsync(x => x.Title == title, Ct));
        Assert.False(await db.Notifications.AsNoTracking().AnyAsync(x => x.Title == title, Ct));
        Assert.False(await db.NotificationJobs.AsNoTracking().AnyAsync(x => x.Notification != null && x.Notification.Title == title, Ct));
        Assert.Equal("News B", (await db.TeamNews.AsNoTracking().SingleAsync(x => x.Id == s.NewsB.Id, Ct)).Title);
    }

    [Fact]
    public async Task CreateHttp_CommitsBeforeDelivery_ProviderFailureOnlyChangesWorkerJob()
    {
        var s = await TeamApiBaselineScenarioBuilder.CreateAsync(factory.Services);
        var push = new RejectPush();
        using var host = factory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<IWebPushService>();
            services.AddSingleton<IWebPushService>(push);
        }));
        await using (var setup = factory.Services.CreateAsyncScope())
        {
            var db = setup.ServiceProvider.GetRequiredService<AppDbContext>();
            db.PushSubscriptions.Add(new PushSubscription
            {
                UserId = s.Pair.UserB.Id, Endpoint = $"https://push.test.invalid/{Guid.NewGuid():N}",
                AuthKey = "synthetic", P256dhKey = "synthetic"
            });
            await db.SaveChangesAsync(Ct);
        }
        using var client = host.CreateClient();
        using var signed = AuthenticatedTestClientFactory.Create(factory, s.Admin);
        client.DefaultRequestHeaders.Authorization = signed.DefaultRequestHeaders.Authorization;
        var title = $"Committed HP82 {Guid.NewGuid():N}";
        using var response = await client.PostAsJsonAsync($"/api/teams/{s.Pair.TeamB.Id}/news",
            new { title, body = "Body", sendNotification = true }, Ct);
        Assert.Equal(200, (int)response.StatusCode);
        var dto = (await response.Content.ReadFromJsonAsync<TeamNewsDto>(Ct))!;
        Assert.Equal(0, push.Calls);
        Guid jobId;
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            Assert.True(await db.TeamNews.AsNoTracking().AnyAsync(x => x.Id == dto.Id && x.Title == title, Ct));
            var notifications = await db.Notifications.AsNoTracking().Where(x => x.Title == title).ToListAsync(Ct);
            Assert.Equal(3, notifications.Count);
            Assert.Equal(new[] { s.Pair.UserB.Id, s.Admin.Id, s.Member.Id }.Order(), notifications.Select(x => x.UserId).Order());
            Assert.All(notifications, x =>
            {
                Assert.Equal(NotificationType.TeamNewsCreated, x.Type);
                Assert.Equal(NotificationCategory.TeamNews, x.Category);
                Assert.Equal($"/teams/{s.Pair.TeamB.Id}", x.Url);
            });
            var jobs = await db.NotificationJobs.AsNoTracking().Where(x => x.Notification != null && x.Notification.Title == title).ToListAsync(Ct);
            Assert.Equal(3, jobs.Count);
            Assert.All(jobs, x => Assert.Equal(NotificationJobStatus.Pending, x.Status));
            jobId = Assert.Single(jobs, x => x.NotificationId == notifications.Single(n => n.UserId == s.Pair.UserB.Id).Id).Id;
        }
        // Delivery runs explicitly in a later scope, using the actual M6 processor.
        await using (var worker = factory.Services.CreateAsyncScope())
        {
            var db = worker.ServiceProvider.GetRequiredService<AppDbContext>();
            push.CheckTransaction = () => Assert.Null(db.Database.CurrentTransaction);
            var processor = new NotificationJobProcessor(db, push, TimeProvider.System,
                Options.Create(new NotificationWorkerOptions()), NullLogger<NotificationJobProcessor>.Instance);
            await processor.ProcessAsync(jobId, Ct);
        }
        Assert.Equal(1, push.Calls);
        Assert.Equal(200, (int)response.StatusCode);
        await using var verify = factory.Services.CreateAsyncScope();
        var persisted = verify.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.Equal(title, (await persisted.TeamNews.AsNoTracking().SingleAsync(x => x.Id == dto.Id, Ct)).Title);
        var job = await persisted.NotificationJobs.AsNoTracking().SingleAsync(x => x.Id == jobId, Ct);
        Assert.Equal(NotificationJobStatus.Pending, job.Status);
        Assert.Equal(1, job.AttemptCount);
        Assert.Equal("provider_transient", job.LastErrorCode);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CreateApplication_UsesClockAndExactCancellationToken_NotificationIsOptional(bool notify)
    {
        var s = await TeamApiBaselineScenarioBuilder.CreateAsync(factory.Services);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var recorder = new TokenRecorder(linked.Token);
        var now = new DateTimeOffset(2032, 4, 5, 6, 7, 8, TimeSpan.Zero);
        using var host = factory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            services.AddDbContext<AppDbContext>(options => options.AddInterceptors(recorder));
            services.RemoveAll<TimeProvider>();
            services.AddSingleton<TimeProvider>(new FixedTimeProvider(now));
        }));
        var title = $"Clock HP82 {Guid.NewGuid():N}";
        TeamNewsDto dto;
        await using (var scope = host.Services.CreateAsyncScope())
        {
            dto = await scope.ServiceProvider.GetRequiredService<ITeamNewsService>().CreateTeamNews(s.Pair.TeamB.Id, s.Admin.Id,
                new CreateTeamNewsRequest { Title = title, Body = "Body", SendNotification = notify }, linked.Token);
        }
        Assert.True(recorder.Commands >= (notify ? 6 : 3));
        Assert.Equal(now.UtcDateTime, dto.CreatedAt);
        Assert.Equal(now.UtcDateTime, dto.UpdatedAt);
        await using var verify = factory.Services.CreateAsyncScope();
        var db = verify.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.Equal(notify ? 3 : 0, await db.Notifications.CountAsync(x => x.Title == title, Ct));
        Assert.Equal(notify ? 3 : 0, await db.NotificationJobs.CountAsync(x => x.Notification != null && x.Notification.Title == title, Ct));
    }

    [Fact]
    public async Task EveryApplicationUseCase_PropagatesCancelledTokenToPostgres()
    {
        var s = await TeamApiBaselineScenarioBuilder.CreateAsync(factory.Services);
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        var token = cancelled.Token;
        var id = s.Pair.TeamB.Id;
        var actor = s.Admin.Id;
        Func<IServiceProvider, Task>[] calls =
        [
            p => p.GetRequiredService<ITeamNewsService>().GetTeamNews(id, true, actor, token),
            p => p.GetRequiredService<ITeamNewsService>().GetNewsFeed(actor, token),
            p => p.GetRequiredService<ITeamNewsService>().CreateTeamNews(id, actor, new CreateTeamNewsRequest { Title = "Cancelled HP82", Body = "Body", SendNotification = true }, token),
            p => p.GetRequiredService<ITeamNewsService>().UpdateTeamNews(id, s.NewsB.Id, actor, new UpdateTeamNewsRequest { Title = "Cancelled HP82", Body = "Body" }, token),
            p => p.GetRequiredService<ITeamNewsService>().DeleteTeamNews(id, s.NewsB.Id, actor, token),
            p => p.GetRequiredService<ITeamMediaService>().EnsureCanUploadTeamMedia(id, actor, token),
            p => p.GetRequiredService<ITeamMediaService>().EnsureCanUploadNewsImage(id, actor, token),
            p => p.GetRequiredService<ITeamMediaService>().SaveTeamMedia(id, actor, "https://test.invalid/cancelled.png", false, token)
        ];
        foreach (var call in calls)
        {
            await using var scope = factory.Services.CreateAsyncScope();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => call(scope.ServiceProvider));
        }
        await using var verify = factory.Services.CreateAsyncScope();
        var db = verify.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.Equal("News B", (await db.TeamNews.AsNoTracking().SingleAsync(x => x.Id == s.NewsB.Id, Ct)).Title);
        Assert.False(await db.TeamNews.AnyAsync(x => x.TeamId == id && x.Title == "Cancelled HP82", Ct));
        Assert.Null((await db.Teams.AsNoTracking().SingleAsync(x => x.Id == id, Ct)).AvatarUrl);
    }

    [Theory]
    [InlineData("avatar")]
    [InlineData("cover")]
    [InlineData("news")]
    public async Task Upload_StorageCancellationPropagatesWithoutFalse502OrDatabaseMutation(string kind)
    {
        var s = await TeamApiBaselineScenarioBuilder.CreateAsync(factory.Services);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var storage = new CancelStorage(linked);
        using var host = factory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<IFileStorageService>();
            services.AddSingleton<IFileStorageService>(storage);
        }));
        await using (var scope = host.Services.CreateAsyncScope())
        {
            var service = scope.ServiceProvider.GetRequiredService<ITeamMediaUploadService>();
            using var bytes = new MemoryStream(new byte[] { 1 });
            var file = new FormFile(bytes, 0, 1, "file", "pixel.png") { Headers = new HeaderDictionary(), ContentType = "image/png" };
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => kind == "news"
                ? (Task)service.UploadNewsImage(s.Pair.TeamB.Id, s.Admin.Id, file, linked.Token)
                : service.UploadTeamMedia(s.Pair.TeamB.Id, s.Admin.Id, file, kind == "cover", linked.Token));
        }
        Assert.Equal(1, storage.Calls);
        await using var verify = factory.Services.CreateAsyncScope();
        var team = await verify.ServiceProvider.GetRequiredService<AppDbContext>().Teams.AsNoTracking().SingleAsync(x => x.Id == s.Pair.TeamB.Id, Ct);
        Assert.Null(team.AvatarUrl);
        Assert.Null(team.CoverImageUrl);
    }

    private sealed class FailInsert(string table) : DbCommandInterceptor
    {
        public bool Failed, NewsWritten, JobsWritten;
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData eventData,
            InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains($"INSERT INTO {table} (", StringComparison.Ordinal))
            {
                Failed = true;
                throw new NpgsqlException("injected database failure", new IOException());
            }
            return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
        }
        public override ValueTask<DbDataReader> ReaderExecutedAsync(DbCommand command, CommandExecutedEventData eventData,
            DbDataReader result, CancellationToken cancellationToken = default)
        {
            NewsWritten |= command.CommandText.Contains("INSERT INTO team_news (", StringComparison.Ordinal);
            JobsWritten |= command.CommandText.Contains("INSERT INTO notification_jobs (", StringComparison.Ordinal);
            return base.ReaderExecutedAsync(command, eventData, result, cancellationToken);
        }
    }

    private sealed class StagedFailure { public bool Failed; }

    private sealed class FailAfterEnqueue(NotificationService actual, AppDbContext db, StagedFailure failure) : INotificationService
    {
        public async Task NotifyTeamAsync(Guid teamId, NotificationType type, NotificationCategory category,
            string title, string body, string? url = null, CancellationToken cancellationToken = default)
        {
            await actual.NotifyTeamAsync(teamId, type, category, title, body, url, cancellationToken);
            Assert.NotNull(db.Database.CurrentTransaction);
            Assert.Equal(3, await db.NotificationJobs.CountAsync(x => x.Notification != null && x.Notification.Title == title, cancellationToken));
            failure.Failed = true;
            throw new InvalidOperationException("injected failure after durable staging");
        }
        public Task NotifyUserAsync(Guid userId, NotificationType type, NotificationCategory category, string title, string body, string? url = null, CancellationToken cancellationToken = default) => throw new InvalidOperationException("Unexpected boundary");
        public Task NotifyUsersAsync(IReadOnlyCollection<Guid> userIds, NotificationType type, NotificationCategory category, string title, string body, string? url = null, CancellationToken cancellationToken = default) => throw new InvalidOperationException("Unexpected boundary");
    }

    private sealed class FailCommit(bool enabled) : DbTransactionInterceptor
    {
        public bool Failed;
        public override ValueTask<InterceptionResult> TransactionCommittingAsync(DbTransaction transaction, TransactionEventData eventData,
            InterceptionResult result, CancellationToken cancellationToken = default)
        {
            if (enabled)
            {
                Failed = true;
                throw new NpgsqlException("injected commit failure", new IOException());
            }
            return base.TransactionCommittingAsync(transaction, eventData, result, cancellationToken);
        }
    }

    private sealed class TokenRecorder(CancellationToken expected) : DbCommandInterceptor
    {
        public int Commands;
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData eventData,
            InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            Commands++;
            Assert.Equal(expected, cancellationToken);
            return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
        }
        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand command, CommandEventData eventData,
            InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            Commands++;
            Assert.Equal(expected, cancellationToken);
            return base.NonQueryExecutingAsync(command, eventData, result, cancellationToken);
        }
    }

    private sealed class CancelStorage(CancellationTokenSource source) : IFileStorageService
    {
        public int Calls;
        public Task<FileStorageUploadResult> UploadAsync(FileStorageUploadRequest request, CancellationToken cancellationToken)
        {
            Calls++;
            Assert.Equal(source.Token, cancellationToken);
            source.Cancel();
            cancellationToken.ThrowIfCancellationRequested();
            throw new InvalidOperationException("Cancellation must throw");
        }
        public Task DeleteAsync(string key, CancellationToken cancellationToken) => throw new InvalidOperationException("No deletion in HP82");
    }

    private sealed class RejectPush : IWebPushService
    {
        public bool IsConfigured => true;
        public int Calls;
        public Action? CheckTransaction;
        public Task<WebPushSendResult> SendAsync(PushSubscription subscription, object payload, CancellationToken cancellationToken = default)
        {
            Calls++;
            CheckTransaction?.Invoke();
            throw new HttpRequestException("synthetic provider outage");
        }
    }
}
