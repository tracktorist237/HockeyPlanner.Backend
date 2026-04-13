using HockeyPlanner.Backend.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace HockeyPlanner.Backend.WebAPI.Services
{
    public class BirthdayPushHostedService : BackgroundService
    {
        private static readonly TimeSpan PollInterval = TimeSpan.FromMinutes(30);
        private readonly IServiceScopeFactory _scopeFactory;
        private readonly ILogger<BirthdayPushHostedService> _logger;
        private readonly string _timeZoneId;

        public BirthdayPushHostedService(
            IServiceScopeFactory scopeFactory,
            IConfiguration configuration,
            ILogger<BirthdayPushHostedService> logger)
        {
            _scopeFactory = scopeFactory;
            _logger = logger;
            _timeZoneId = configuration["BIRTHDAY_TIMEZONE"] ??
                          Environment.GetEnvironmentVariable("BIRTHDAY_TIMEZONE") ??
                          "Europe/Moscow";
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    await ProcessBirthdays(stoppingToken);
                }
                catch (OperationCanceledException)
                {
                    // graceful shutdown
                }
                catch (Exception exception)
                {
                    _logger.LogError(exception, "Birthday push background job failed.");
                }

                await Task.Delay(PollInterval, stoppingToken);
            }
        }

        private async Task ProcessBirthdays(CancellationToken cancellationToken)
        {
            using var scope = _scopeFactory.CreateScope();
            var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var webPushService = scope.ServiceProvider.GetRequiredService<IWebPushService>();

            if (!webPushService.IsConfigured)
            {
                return;
            }

            var timeZone = ResolveTimeZone(_timeZoneId);
            var nowUtc = DateTime.UtcNow;
            var todayLocal = TimeZoneInfo.ConvertTimeFromUtc(nowUtc, timeZone).Date;

            var users = await dbContext.Users
                .AsNoTracking()
                .Where(user => user.BirthDate.HasValue)
                .ToListAsync(cancellationToken);

            var birthdayUsers = users
                .Where(user =>
                {
                    var birthUtc = NormalizeToUtc(user.BirthDate!.Value);
                    var birthLocal = TimeZoneInfo.ConvertTimeFromUtc(birthUtc, timeZone);
                    return birthLocal.Month == todayLocal.Month && birthLocal.Day == todayLocal.Day;
                })
                .OrderBy(user => user.LastName)
                .ThenBy(user => user.FirstName)
                .ToList();

            if (birthdayUsers.Count == 0)
            {
                return;
            }

            var subscriptions = await dbContext.PushSubscriptions.ToListAsync(cancellationToken);
            if (subscriptions.Count == 0)
            {
                return;
            }

            var payload = BuildBirthdayPayload(birthdayUsers, todayLocal, timeZone);
            var sentCount = 0;
            var removedCount = 0;

            foreach (var subscription in subscriptions)
            {
                if (WasSentToday(subscription.LastBirthdayNotificationAt, todayLocal, timeZone))
                {
                    continue;
                }

                var sendResult = await webPushService.SendAsync(subscription, payload, cancellationToken);
                if (sendResult.IsSuccess)
                {
                    subscription.LastBirthdayNotificationAt = nowUtc;
                    sentCount++;
                    continue;
                }

                if (sendResult.ShouldRemoveSubscription)
                {
                    dbContext.PushSubscriptions.Remove(subscription);
                    removedCount++;
                }
            }

            if (sentCount > 0 || removedCount > 0)
            {
                await dbContext.SaveChangesAsync(cancellationToken);
                _logger.LogInformation(
                    "Birthday push sent to {SentCount} subscriptions, removed {RemovedCount}.",
                    sentCount,
                    removedCount);
            }
        }

        private static object BuildBirthdayPayload(List<Core.Entities.User> users, DateTime todayLocal, TimeZoneInfo timeZone)
        {
            const string title = "🎂 День рождения в команде";

            if (users.Count == 1)
            {
                var birthdayUser = users[0];
                var birthLocal = TimeZoneInfo.ConvertTimeFromUtc(
                    NormalizeToUtc(birthdayUser.BirthDate!.Value),
                    timeZone);
                var age = todayLocal.Year - birthLocal.Year;
                var singleBody = $"Сегодня день рождения у {birthdayUser.LastName} {birthdayUser.FirstName}. Поздравляем! ({age})";
                return new { title, body = singleBody, url = "/events" };
            }

            var topNames = users
                .Take(2)
                .Select(value => $"{value.LastName} {value.FirstName}")
                .ToList();

            var manyBody = users.Count == 2
                ? $"Сегодня день рождения у: {string.Join(" и ", topNames)}."
                : $"Сегодня день рождения у: {string.Join(", ", topNames)} и еще {users.Count - 2}.";

            return new { title, body = manyBody, url = "/events" };
        }

        private static bool WasSentToday(DateTime? sentAtUtc, DateTime todayLocal, TimeZoneInfo timeZone)
        {
            if (!sentAtUtc.HasValue)
            {
                return false;
            }

            var localSentDate = TimeZoneInfo.ConvertTimeFromUtc(NormalizeToUtc(sentAtUtc.Value), timeZone).Date;
            return localSentDate == todayLocal;
        }

        private static DateTime NormalizeToUtc(DateTime value)
        {
            return value.Kind switch
            {
                DateTimeKind.Utc => value,
                DateTimeKind.Local => value.ToUniversalTime(),
                _ => DateTime.SpecifyKind(value, DateTimeKind.Utc),
            };
        }

        private static TimeZoneInfo ResolveTimeZone(string timeZoneId)
        {
            try
            {
                return TimeZoneInfo.FindSystemTimeZoneById(timeZoneId);
            }
            catch
            {
                return TimeZoneInfo.Utc;
            }
        }
    }
}

