namespace HockeyPlanner.Backend.WebAPI.Options
{
    public sealed class MigrationOptions
    {
        public string SigningKey { get; set; } = string.Empty;
        public int TokenLifetimeMinutes { get; set; } = 5;
        public string TargetFrontendUrl { get; set; } = "https://hockeyplanner.ru";
        public bool RenderMigrationMode { get; set; }
    }
}
