namespace HockeyPlanner.Backend.WebAPI.Models.Auth
{
    public sealed class RegisterRequest
    {
        public string FirstName { get; set; } = string.Empty;
        public string LastName { get; set; } = string.Empty;
        public string Email { get; set; } = string.Empty;
        public string Password { get; set; } = string.Empty;
        public int? JerseyNumber { get; set; }
    }

    public sealed class LinkPlayerRequest
    {
        public Guid UserId { get; set; }
    }

    public sealed class ChangeEmailRequest
    {
        public string NewEmail { get; set; } = string.Empty;
        public string Password { get; set; } = string.Empty;
    }

    public sealed class ChangePasswordRequest
    {
        public string CurrentPassword { get; set; } = string.Empty;
        public string NewPassword { get; set; } = string.Empty;
    }

    public sealed class LoginRequest
    {
        public string Email { get; set; } = string.Empty;
        public string Password { get; set; } = string.Empty;
    }

    public sealed class RefreshTokenRequest
    {
        public string RefreshToken { get; set; } = string.Empty;
    }

    public sealed class LogoutRequest
    {
        public string? RefreshToken { get; set; }
    }

    public sealed class ConfirmEmailRequest
    {
        public string Token { get; set; } = string.Empty;
    }

    public sealed class ForgotPasswordRequest
    {
        public string Email { get; set; } = string.Empty;
    }

    public sealed class ResetPasswordRequest
    {
        public string Token { get; set; } = string.Empty;
        public string NewPassword { get; set; } = string.Empty;
    }
}
