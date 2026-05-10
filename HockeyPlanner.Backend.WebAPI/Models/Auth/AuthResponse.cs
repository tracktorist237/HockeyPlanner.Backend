namespace HockeyPlanner.Backend.WebAPI.Models.Auth
{
    public sealed class AuthResponse
    {
        public string AccessToken { get; set; } = string.Empty;
        public string RefreshToken { get; set; } = string.Empty;
        public DateTime AccessTokenExpiresAt { get; set; }
        public AuthUserResponse User { get; set; } = new();
    }
}
