using HockeyPlanner.Backend.Application.Abstractions.Services;
using HockeyPlanner.Backend.Core.Enums;
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
                    _logger.LogError(exception, "Birthday notification background job failed.");
                }

                await Task.Delay(PollInterval, stoppingToken);
            }
        }

        private async Task ProcessBirthdays(CancellationToken cancellationToken)
        {
            using var scope = _scopeFactory.CreateScope();
            var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var notificationService = scope.ServiceProvider.GetRequiredService<INotificationService>();

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

            var dueSubscriptions = await dbContext.PushSubscriptions
                .Where(subscription => subscription.IsActive && subscription.UserId.HasValue)
                .ToListAsync(cancellationToken);

            dueSubscriptions = dueSubscriptions
                .Where(subscription => !WasSentToday(subscription.LastBirthdayNotificationAt, todayLocal, timeZone))
                .ToList();

            if (dueSubscriptions.Count == 0)
            {
                return;
            }

            var targetUserIds = dueSubscriptions
                .Select(subscription => subscription.UserId!.Value)
                .Distinct()
                .ToList();
            var birthdayUserIds = birthdayUsers
                .Select(user => user.Id)
                .ToHashSet();
            var membershipUserIds = targetUserIds
                .Concat(birthdayUserIds)
                .Distinct()
                .ToList();
            var memberships = await dbContext.TeamMemberships
                .AsNoTracking()
                .Where(membership => membershipUserIds.Contains(membership.UserId))
                .Select(membership => new { membership.UserId, membership.TeamId })
                .ToListAsync(cancellationToken);

            var teamIdsByUserId = memberships
                .GroupBy(membership => membership.UserId)
                .ToDictionary(
                    group => group.Key,
                    group => group.Select(membership => membership.TeamId).ToHashSet());
            var notifiedUserIds = new HashSet<Guid>();

            foreach (var targetUserId in targetUserIds)
            {
                if (!teamIdsByUserId.TryGetValue(targetUserId, out var targetTeamIds) || targetTeamIds.Count == 0)
                {
                    continue;
                }

                var visibleBirthdayUsers = birthdayUsers
                    .Where(user =>
                        user.Id != targetUserId &&
                        teamIdsByUserId.TryGetValue(user.Id, out var birthdayTeamIds) &&
                        birthdayTeamIds.Overlaps(targetTeamIds))
                    .ToList();

                if (visibleBirthdayUsers.Count == 0)
                {
                    continue;
                }

                var notification = BuildBirthdayNotification(visibleBirthdayUsers, todayLocal, timeZone);
                await notificationService.NotifyUserAsync(
                    targetUserId,
                    NotificationType.BirthdayReminder,
                    NotificationCategory.Birthdays,
                    notification.Title,
                    notification.Body,
                    notification.Url,
                    cancellationToken);
                notifiedUserIds.Add(targetUserId);
            }

            if (notifiedUserIds.Count == 0)
            {
                return;
            }

            foreach (var subscription in dueSubscriptions)
            {
                if (!subscription.UserId.HasValue || !notifiedUserIds.Contains(subscription.UserId.Value))
                {
                    continue;
                }

                subscription.LastBirthdayNotificationAt = nowUtc;
                subscription.UpdatedAt = nowUtc;
            }

            await dbContext.SaveChangesAsync(cancellationToken);
            _logger.LogInformation(
                "Birthday notifications created for {UserCount} users.",
                notifiedUserIds.Count);
        }

        private static (string Title, string Body, string Url) BuildBirthdayNotification(
            List<Core.Entities.User> users,
            DateTime todayLocal,
            TimeZoneInfo timeZone)
        {
            const string title = "День рождения в команде";

            if (users.Count == 1)
            {
                var birthdayUser = users[0];
                var birthLocal = TimeZoneInfo.ConvertTimeFromUtc(
                    NormalizeToUtc(birthdayUser.BirthDate!.Value),
                    timeZone);
                var age = todayLocal.Year - birthLocal.Year;
                var singleBody = $"Сегодня день рождения у {birthdayUser.LastName} {birthdayUser.FirstName}. Поздравляем! ({age})";
                return (title, singleBody, "/events");
            }

            var topNames = users
                .Take(2)
                .Select(value => $"{value.LastName} {value.FirstName}")
                .ToList();

            var manyBody = users.Count == 2
                ? $"Сегодня день рождения у: {string.Join(" и ", topNames)}."
                : $"Сегодня день рождения у: {string.Join(", ", topNames)} и еще {users.Count - 2}.";

            return (title, manyBody, "/events");
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
