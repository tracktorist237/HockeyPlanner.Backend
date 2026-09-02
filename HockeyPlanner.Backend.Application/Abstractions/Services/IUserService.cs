using HockeyPlanner.Backend.Shared.Models.Users;

namespace HockeyPlanner.Backend.Application.Abstractions.Services;

public interface IUserService
{
    Task<IReadOnlyList<UserSummaryDto>> GetDirectory(
        Guid viewerUserId,
        CancellationToken cancellationToken);

    Task<BirthdaysTodayResponse> GetBirthdaysToday(
        Guid viewerUserId,
        CancellationToken cancellationToken);

    Task<UserProfileDto> GetProfile(
        Guid targetUserId,
        Guid? viewerUserId,
        Guid? teamId,
        CancellationToken cancellationToken);

    Task<UserPrivacySettingsDto> GetPrivacySettings(
        Guid targetUserId,
        Guid actorUserId,
        CancellationToken cancellationToken);

    Task<UserPrivacySettingsDto> UpdatePrivacySettings(
        Guid targetUserId,
        Guid actorUserId,
        UpdateUserPrivacySettingsRequest request,
        CancellationToken cancellationToken);

    Task<UserProfileDto> UpdateUser(
        Guid targetUserId,
        Guid actorUserId,
        UpdateUserRequest request,
        CancellationToken cancellationToken);

    Task EnsureAvatarUploadAllowed(
        Guid targetUserId,
        Guid actorUserId,
        CancellationToken cancellationToken);

    Task<UserProfileDto> UpdateAvatar(
        Guid targetUserId,
        Guid actorUserId,
        string photoUrl,
        CancellationToken cancellationToken);

    Task RejectUserDeletion(
        Guid targetUserId,
        CancellationToken cancellationToken);

    Task RejectLegacyUserCreation(CancellationToken cancellationToken);
}
