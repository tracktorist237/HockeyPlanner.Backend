using System.Security.Claims;

namespace HockeyPlanner.Backend.WebAPI.Extensions
{
    public static class ClaimsPrincipalExtensions
    {
        public static Guid? GetUserId(this ClaimsPrincipal user)
        {
            var value = user.FindFirstValue(ClaimTypes.NameIdentifier) ?? user.FindFirstValue("sub");
            if (Guid.TryParse(value, out var userId))
            {
                return userId;
            }

            return null;
        }
    }
}
