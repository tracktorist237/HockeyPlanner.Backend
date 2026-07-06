using HockeyPlanner.Backend.Core.Entities.Base;
using HockeyPlanner.Backend.Core.Enums;

namespace HockeyPlanner.Backend.Core.Entities
{
    public class UserPrivacySettings : Entity
    {
        public Guid UserId { get; set; }
        public User User { get; set; } = null!;

        public UserDataVisibility EmailVisibility { get; set; } = UserDataVisibility.Teammates;
        public UserDataVisibility PhoneVisibility { get; set; } = UserDataVisibility.TeamAdmins;
        public UserDataVisibility BirthDateVisibility { get; set; } = UserDataVisibility.Teammates;
        public UserDataVisibility PhysicalVisibility { get; set; } = UserDataVisibility.Teammates;
        public UserDataVisibility HockeyProfileVisibility { get; set; } = UserDataVisibility.Teammates;
        public UserDataVisibility SpbhlProfileVisibility { get; set; } = UserDataVisibility.Teammates;
    }
}
