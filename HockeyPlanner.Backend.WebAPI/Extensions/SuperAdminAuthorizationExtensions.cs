using HockeyPlanner.Backend.Core.Enums;
using HockeyPlanner.Backend.Infrastructure.Data;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace HockeyPlanner.Backend.WebAPI.Extensions
{
    public static class SuperAdminAuthorizationExtensions
    {
        public static async Task<bool> IsSuperAdminAsync(this ControllerBase controller, AppDbContext context, CancellationToken cancellationToken = default)
        {
            var userId = controller.User.GetUserId();
            if (!userId.HasValue)
                return false;

            return await context.Users
                .AsNoTracking()
                .AnyAsync(user => user.Id == userId.Value && user.AppRole == AppRole.SuperAdmin, cancellationToken);
        }
    }
}
