namespace HockeyPlanner.Backend.WebAPI.Services
{
    public static class AppVersionInfo
    {
        public const string FallbackVersion = "0.0.0-dev";

        public static string GetVersion(IConfiguration configuration)
        {
            return ConfiguredValue(configuration["APP_VERSION"], FallbackVersion);
        }

        public static string? GetCommit(IConfiguration configuration)
        {
            return OptionalConfiguredValue(configuration["APP_COMMIT"]);
        }

        public static string? GetBuildTime(IConfiguration configuration)
        {
            return OptionalConfiguredValue(configuration["APP_BUILD_TIME"]);
        }

        private static string ConfiguredValue(string? value, string fallbackValue)
        {
            return string.IsNullOrWhiteSpace(value)
                ? fallbackValue
                : value;
        }

        private static string? OptionalConfiguredValue(string? value)
        {
            return string.IsNullOrWhiteSpace(value)
                ? null
                : value;
        }
    }
}
