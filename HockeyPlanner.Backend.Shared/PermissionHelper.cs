using HockeyPlanner.Backend.Core.Enums;

namespace HockeyPlanner.Backend.Shared
{
    public static class PermissionHelper
    {
        public static bool CheckCreatePermission(UserRole role)
        {
            return role == UserRole.Coach ||
                   role == UserRole.Manager ||
                   role == UserRole.Captain;
        }
    }
}
