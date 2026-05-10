using HockeyPlanner.Backend.Core.Entities;

namespace HockeyPlanner.Backend.WebAPI.Services
{
    public interface IAuthTokenService
    {
        string CreateAccessToken(User user);
        string CreateOpaqueToken();
        string HashToken(string token);
    }
}
