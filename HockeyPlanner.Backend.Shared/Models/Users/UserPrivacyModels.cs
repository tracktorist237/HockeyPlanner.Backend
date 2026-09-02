using HockeyPlanner.Backend.Core.Enums;

namespace HockeyPlanner.Backend.Shared.Models.Users;

public class UserPrivacySettingsDto
{
    public Guid UserId { get; set; }
    public UserDataVisibility EmailVisibility { get; set; } = UserDataVisibility.Teammates;
    public UserDataVisibility PhoneVisibility { get; set; } = UserDataVisibility.TeamAdmins;
    public UserDataVisibility BirthDateVisibility { get; set; } = UserDataVisibility.Teammates;
    public UserDataVisibility PhysicalVisibility { get; set; } = UserDataVisibility.Teammates;
    public UserDataVisibility HockeyProfileVisibility { get; set; } = UserDataVisibility.Teammates;
    public UserDataVisibility SpbhlProfileVisibility { get; set; } = UserDataVisibility.Teammates;
}

public class UpdateUserPrivacySettingsRequest
{
    public UserDataVisibility EmailVisibility { get; set; }
    public UserDataVisibility PhoneVisibility { get; set; }
    public UserDataVisibility BirthDateVisibility { get; set; }
    public UserDataVisibility PhysicalVisibility { get; set; }
    public UserDataVisibility HockeyProfileVisibility { get; set; }
    public UserDataVisibility SpbhlProfileVisibility { get; set; }
}

public class UserProfileDto
{
    public Guid Id { get; set; }
    public string FirstName { get; set; } = string.Empty;
    public string LastName { get; set; } = string.Empty;
    public string? Email { get; set; }
    public bool EmailConfirmed { get; set; }
    public string? Phone { get; set; }
    public string? PhotoUrl { get; set; }
    public Guid? SpbhlPlayerId { get; set; }
    public UserRole Role { get; set; }
    public AppRole AppRole { get; set; }
    public int? JerseyNumber { get; set; }
    public Position? PrimaryPosition { get; set; }
    public Handedness? Handedness { get; set; }
    public int? Height { get; set; }
    public int? Weight { get; set; }
    public DateTime? BirthDate { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? UpdatedAt { get; set; }
    public string FullName { get; set; } = string.Empty;
}
