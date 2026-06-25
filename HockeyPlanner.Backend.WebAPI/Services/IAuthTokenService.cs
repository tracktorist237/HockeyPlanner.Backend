using HockeyPlanner.Backend.Core.Entities;
using HockeyPlanner.Backend.WebAPI.Models.Auth;

namespace HockeyPlanner.Backend.WebAPI.Services
{
    public interface IAuthTokenService
    {
        string CreateAccessToken(User user);
        string CreateMigrationToken(User user, DateTime expiresAt);
        MigrationTokenClaims ValidateMigrationToken(string token);
        string CreateOpaqueToken();
        string HashToken(string token);
    }
}
