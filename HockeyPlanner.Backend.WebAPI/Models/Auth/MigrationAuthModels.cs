namespace HockeyPlanner.Backend.WebAPI.Models.Auth
{
    public sealed class MigrationTokenResponse
    {
        public string MigrationToken { get; set; } = string.Empty;
        public string TargetUrl { get; set; } = string.Empty;
        public DateTime ExpiresAt { get; set; }
    }

    public sealed class MigrateLoginRequest
    {
        public string Token { get; set; } = string.Empty;
    }

    public sealed class MigrationTokenClaims
    {
        public Guid UserId { get; set; }
        public string Email { get; set; } = string.Empty;
        public string Purpose { get; set; } = string.Empty;
    }
}
