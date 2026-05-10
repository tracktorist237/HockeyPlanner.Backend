namespace HockeyPlanner.Backend.WebAPI.Options
{
    public sealed class JwtOptions
    {
        public string Issuer { get; set; } = "HockeyPlanner";
        public string Audience { get; set; } = "HockeyPlanner.Frontend";
        public string SigningKey { get; set; } = string.Empty;
        public int AccessTokenMinutes { get; set; } = 15;
        public int RefreshTokenDays { get; set; } = 60;
        public int EmailTokenHours { get; set; } = 24;
        public int PasswordResetTokenMinutes { get; set; } = 30;
    }
}
