using HockeyPlanner.Backend.Core.Entities;
using HockeyPlanner.Backend.Core.Enums;
using HockeyPlanner.Backend.Infrastructure.Data;
using HockeyPlanner.Backend.IntegrationTests.Infrastructure;
using HockeyPlanner.Backend.WebAPI.Options;
using HockeyPlanner.Backend.WebAPI.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace HockeyPlanner.Backend.IntegrationTests.Services;

[Collection(IntegrationTestCollection.Name)]
public sealed class AuthEmailOutboxTests(HockeyPlannerWebApplicationFactory factory)
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task EmailWork_IsEncryptedAtomicAndRecoverable(bool rollback)
    {
        var token = TestContext.Current.CancellationToken;
        var user = new User { FirstName = "Email", LastName = "Test", Email = $"outbox-{Guid.NewGuid():N}@example.invalid" };
        var raw = Guid.NewGuid().ToString("N");
        Guid jobId;
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var tokens = scope.ServiceProvider.GetRequiredService<IAuthTokenService>();
            var record = new EmailConfirmationToken { UserId = user.Id, TokenHash = tokens.HashToken(raw), ExpiresAt = DateTime.UtcNow.AddHours(1) };
            await using var tx = await db.Database.BeginTransactionAsync(token);
            db.AddRange(user, record);
            scope.ServiceProvider.GetRequiredService<AuthEmailOutbox>().Stage(user.Id, record.Id, raw, NotificationJobKind.EmailConfirmation);
            await db.SaveChangesAsync(token);
            var job = await db.NotificationJobs.SingleAsync(value => value.UserId == user.Id, token);
            jobId = job.Id;
            Assert.NotNull(job.ProtectedPayload);
            Assert.DoesNotContain(raw, job.ProtectedPayload);
            Assert.DoesNotContain(user.Email, job.ProtectedPayload);
            if (rollback) await tx.RollbackAsync(token);
            else await tx.CommitAsync(token);
        }
        await using var recovered = factory.Services.CreateAsyncScope();
        var context = recovered.ServiceProvider.GetRequiredService<AppDbContext>();
        var persisted = await context.NotificationJobs.SingleOrDefaultAsync(value => value.Id == jobId, token);
        Assert.Equal(!rollback, await context.Users.AnyAsync(value => value.Id == user.Id, token));
        Assert.Equal(!rollback, await context.EmailConfirmationTokens.AnyAsync(value => value.UserId == user.Id, token));
        if (rollback) { Assert.Null(persisted); return; }
        var sender = new CaptureSender();
        var outbox = new AuthEmailOutbox(context, recovered.ServiceProvider.GetRequiredService<IOptions<JwtOptions>>(),
            TimeProvider.System, recovered.ServiceProvider.GetRequiredService<IAuthTokenService>(), sender);
        await outbox.DeliverAsync(persisted!, token);
        Assert.Equal(raw, sender.Token);
        Assert.Equal(user.Id, sender.UserId);
        var recordToExpire = await context.EmailConfirmationTokens.SingleAsync(value => value.UserId == user.Id, token);
        recordToExpire.UsedAt = DateTime.UtcNow;
        await context.SaveChangesAsync(token);
        sender.Token = null;
        await outbox.DeliverAsync(persisted!, token);
        Assert.Null(sender.Token);
    }

    [Theory]
    [InlineData(System.Net.HttpStatusCode.ServiceUnavailable, NotificationJobStatus.Pending)]
    [InlineData(System.Net.HttpStatusCode.TooManyRequests, NotificationJobStatus.Pending)]
    [InlineData(System.Net.HttpStatusCode.BadRequest, NotificationJobStatus.Failed)]
    public async Task Worker_ClassifiesEmailProviderFailureAndRecovers(System.Net.HttpStatusCode status, NotificationJobStatus expected)
    {
        var token = TestContext.Current.CancellationToken;
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var tokens = scope.ServiceProvider.GetRequiredService<IAuthTokenService>();
        var options = scope.ServiceProvider.GetRequiredService<IOptions<JwtOptions>>();
        var now = DateTimeOffset.UtcNow;
        var user = new User { FirstName = "Retry", LastName = "Email" };
        var raw = Guid.NewGuid().ToString("N");
        var record = new EmailConfirmationToken { UserId = user.Id, TokenHash = tokens.HashToken(raw), ExpiresAt = now.AddHours(1).UtcDateTime };
        var sender = new CaptureSender { Error = new HttpRequestException("safe failure", null, status) };
        var clock = new FixedTimeProvider(now);
        var emails = new AuthEmailOutbox(db, options, clock, tokens, sender);
        db.AddRange(user, record);
        emails.Stage(user.Id, record.Id, raw, NotificationJobKind.EmailConfirmation);
        await db.SaveChangesAsync(token);
        var job = await db.NotificationJobs.SingleAsync(value => value.UserId == user.Id, token);
        var settings = Options.Create(new NotificationWorkerOptions());
        var push = scope.ServiceProvider.GetRequiredService<IWebPushService>();
        var logger = Microsoft.Extensions.Logging.Abstractions.NullLogger<NotificationJobProcessor>.Instance;
        await new NotificationJobProcessor(db, push, clock, settings, logger, emails).ProcessAsync(job.Id, token);
        Assert.Equal(expected, job.Status);
        sender.Error = null;
        await new NotificationJobProcessor(db, push, new FixedTimeProvider(now.AddMinutes(1)), settings, logger, emails).ProcessAsync(job.Id, token);
        Assert.Equal(expected == NotificationJobStatus.Pending ? NotificationJobStatus.Succeeded : NotificationJobStatus.Failed, job.Status);
        Assert.Equal(expected == NotificationJobStatus.Pending ? 2 : 1, sender.Calls);
        Assert.Null(job.ProtectedPayload);
    }

    private sealed class CaptureSender : IAuthEmailSender
    {
        public string? Token;
        public Guid UserId;
        public Exception? Error;
        public int Calls;
        public Task SendEmailConfirmation(User user, string token, CancellationToken cancellationToken = default)
        { Calls++; if (Error is not null) throw Error; Token = token; UserId = user.Id; return Task.CompletedTask; }
        public Task SendPasswordReset(User user, string token, CancellationToken cancellationToken = default)
            => SendEmailConfirmation(user, token, cancellationToken);
    }
}
