using HockeyPlanner.Backend.Core.Enums;

namespace HockeyPlanner.Backend.WebAPI.Models.Auth
{
    public sealed class AuthUserResponse
    {
        public Guid Id { get; set; }
        public string FirstName { get; set; } = string.Empty;
        public string LastName { get; set; } = string.Empty;
        public string? Email { get; set; }
        public bool EmailConfirmed { get; set; }
        public int? JerseyNumber { get; set; }
        public UserRole Role { get; set; }
        public string? PhotoUrl { get; set; }
        public Guid? SpbhlPlayerId { get; set; }
        public string FullName { get; set; } = string.Empty;
    }
}
