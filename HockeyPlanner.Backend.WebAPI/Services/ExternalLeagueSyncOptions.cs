namespace HockeyPlanner.Backend.WebAPI.Services;

public sealed class ExternalLeagueSyncOptions
{
    public const string SectionName = "ExternalLeagueSync";
    public bool Enabled { get; set; }
    public int PollIntervalMinutes { get; set; } = 30;
    public int DailySyncHour { get; set; } = 5;
    public int MaxParallelTeams { get; set; } = 3;
    public int MaxRetryAttempts { get; set; } = 2;
    public int RetryDelaySeconds { get; set; } = 5;
}

public static class ExternalLeagueSyncSchedule
{
    public static TimeZoneInfo ResolveMoscowTimeZone()
    {
        foreach (var id in new[] { "Europe/Moscow", "Russian Standard Time" })
        {
            try { return TimeZoneInfo.FindSystemTimeZoneById(id); }
            catch (TimeZoneNotFoundException) { }
            catch (InvalidTimeZoneException) { }
        }
        throw new InvalidOperationException("Moscow time zone is unavailable.");
    }

    public static DateTimeOffset? GetCurrentThresholdUtc(DateTimeOffset nowUtc, int dailySyncHour)
    {
        var zone = ResolveMoscowTimeZone();
        var localNow = TimeZoneInfo.ConvertTime(nowUtc, zone);
        var localThreshold = new DateTime(localNow.Year, localNow.Month, localNow.Day, dailySyncHour, 0, 0, DateTimeKind.Unspecified);
        if (localNow.DateTime < localThreshold) return null;
        return new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(localThreshold, zone), TimeSpan.Zero);
    }

    public static bool IsTeamDue(DateTimeOffset nowUtc, int dailySyncHour, IReadOnlyCollection<DateTime?> linkSuccessfulAt)
    {
        if (linkSuccessfulAt.Count == 0) return false;
        var threshold = GetCurrentThresholdUtc(nowUtc, dailySyncHour);
        return threshold.HasValue && linkSuccessfulAt.Any(value => !value.HasValue || value.Value < threshold.Value.UtcDateTime);
    }
}
