using HockeyPlanner.Backend.Application.Abstractions.Services;
using HockeyPlanner.Backend.Core.Entities;
using HockeyPlanner.Backend.Core.Enums;
using HockeyPlanner.Backend.Core.Exceptions;
using HockeyPlanner.Backend.Infrastructure.Data;
using HockeyPlanner.Backend.Shared.Models.Users;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;

namespace HockeyPlanner.Backend.Application.Implementations.Services;

internal sealed class UserService : IUserService
{
    private readonly AppDbContext _context;
    private readonly TimeProvider _timeProvider;
    private readonly IConfiguration _configuration;

    public UserService(
        AppDbContext context,
        TimeProvider timeProvider,
        IConfiguration configuration)
    {
        _context = context;
        _timeProvider = timeProvider;
        _configuration = configuration;
    }

    public async Task<IReadOnlyList<UserSummaryDto>> GetDirectory(
        Guid viewerUserId,
        CancellationToken cancellationToken)
    {
        var viewerIsSuperAdmin = await _context.Users
            .AsNoTracking()
            .Where(user => user.Id == viewerUserId)
            .Select(user => user.AppRole == AppRole.SuperAdmin)
            .SingleOrDefaultAsync(cancellationToken);

        return await _context.Users
            .AsNoTracking()
            .Select(user => new
            {
                user.Id,
                user.PhotoUrl,
                user.PrimaryPosition,
                HockeyProfileVisibility = _context.UserPrivacySettings
                    .Where(settings => settings.UserId == user.Id)
                    .Select(settings => (UserDataVisibility?)settings.HockeyProfileVisibility)
                    .FirstOrDefault() ?? UserDataVisibility.Teammates,
                IsTeammate = _context.TeamMemberships.Any(targetMembership =>
                    targetMembership.UserId == user.Id &&
                    _context.TeamMemberships.Any(viewerMembership =>
                        viewerMembership.UserId == viewerUserId &&
                        viewerMembership.TeamId == targetMembership.TeamId)),
                IsTeamAdmin = _context.TeamMemberships.Any(targetMembership =>
                    targetMembership.UserId == user.Id &&
                    _context.TeamMemberships.Any(viewerMembership =>
                        viewerMembership.UserId == viewerUserId &&
                        viewerMembership.TeamId == targetMembership.TeamId &&
                        (viewerMembership.Role == TeamMemberRole.Owner ||
                         viewerMembership.Role == TeamMemberRole.Admin))),
            })
            .Select(user => new UserSummaryDto
            {
                Id = user.Id,
                PhotoUrl = user.PhotoUrl,
                PrimaryPosition = viewerIsSuperAdmin ||
                                  user.Id == viewerUserId ||
                                  user.HockeyProfileVisibility == UserDataVisibility.Everyone ||
                                  (user.HockeyProfileVisibility == UserDataVisibility.Teammates && user.IsTeammate) ||
                                  (user.HockeyProfileVisibility == UserDataVisibility.TeamAdmins && user.IsTeamAdmin)
                    ? user.PrimaryPosition
                    : null,
            })
            .ToListAsync(cancellationToken);
    }

    public async Task<BirthdaysTodayResponse> GetBirthdaysToday(
        Guid viewerUserId,
        CancellationToken cancellationToken)
    {
        var timeZone = ResolveBirthdayTimeZone();
        var today = TimeZoneInfo.ConvertTime(_timeProvider.GetUtcNow(), timeZone).Date;
        var viewerTeamIds = await _context.TeamMemberships
            .AsNoTracking()
            .Where(membership => membership.UserId == viewerUserId)
            .Select(membership => membership.TeamId)
            .ToListAsync(cancellationToken);

        if (viewerTeamIds.Count == 0)
        {
            return EmptyBirthdayResponse(today);
        }

        var viewerIsSuperAdmin = await _context.Users
            .AsNoTracking()
            .Where(user => user.Id == viewerUserId)
            .Select(user => user.AppRole == AppRole.SuperAdmin)
            .SingleOrDefaultAsync(cancellationToken);

        var candidates = await _context.Users
            .AsNoTracking()
            .Where(user => user.BirthDate.HasValue && user.Id != viewerUserId)
            .Where(user => _context.TeamMemberships.Any(membership =>
                membership.UserId == user.Id && viewerTeamIds.Contains(membership.TeamId)))
            .Select(user => new
            {
                user.Id,
                user.FirstName,
                user.LastName,
                user.JerseyNumber,
                BirthDate = user.BirthDate!.Value,
                BirthDateVisibility = _context.UserPrivacySettings
                    .Where(settings => settings.UserId == user.Id)
                    .Select(settings => (UserDataVisibility?)settings.BirthDateVisibility)
                    .FirstOrDefault() ?? UserDataVisibility.Teammates,
                ViewerIsTeamAdmin = _context.TeamMemberships.Any(targetMembership =>
                    targetMembership.UserId == user.Id &&
                    viewerTeamIds.Contains(targetMembership.TeamId) &&
                    _context.TeamMemberships.Any(viewerMembership =>
                        viewerMembership.UserId == viewerUserId &&
                        viewerMembership.TeamId == targetMembership.TeamId &&
                        (viewerMembership.Role == TeamMemberRole.Owner ||
                         viewerMembership.Role == TeamMemberRole.Admin))),
            })
            .ToListAsync(cancellationToken);

        var users = candidates
            .Where(candidate =>
                viewerIsSuperAdmin ||
                candidate.BirthDateVisibility == UserDataVisibility.Everyone ||
                candidate.BirthDateVisibility == UserDataVisibility.Teammates ||
                (candidate.BirthDateVisibility == UserDataVisibility.TeamAdmins && candidate.ViewerIsTeamAdmin))
            .Select(candidate => new
            {
                Candidate = candidate,
                LocalBirthDate = TimeZoneInfo.ConvertTimeFromUtc(NormalizeToUtc(candidate.BirthDate), timeZone),
            })
            .Where(candidate =>
                candidate.LocalBirthDate.Month == today.Month &&
                candidate.LocalBirthDate.Day == today.Day)
            .Select(candidate => new BirthdayUserDto
            {
                UserId = candidate.Candidate.Id,
                FirstName = candidate.Candidate.FirstName,
                LastName = candidate.Candidate.LastName,
                JerseyNumber = candidate.Candidate.JerseyNumber,
                Age = today.Year - candidate.LocalBirthDate.Year,
            })
            .OrderBy(user => user.LastName)
            .ThenBy(user => user.FirstName)
            .ToList();

        return new BirthdaysTodayResponse
        {
            Date = today.ToString("yyyy-MM-dd"),
            Users = users,
        };
    }

    public async Task<UserProfileDto> GetProfile(
        Guid targetUserId,
        Guid? viewerUserId,
        Guid? teamId,
        CancellationToken cancellationToken)
    {
        var user = await _context.Users
            .AsNoTracking()
            .FirstOrDefaultAsync(value => value.Id == targetUserId, cancellationToken);

        if (user == null)
        {
            throw new NotFoundException("Пользователь не найден.");
        }

        var settings = await GetOrCreatePrivacySettings(targetUserId, cancellationToken);
        var viewerContext = await BuildViewerContext(
            targetUserId,
            viewerUserId,
            teamId,
            cancellationToken);

        return ToProfileDto(user, settings, viewerContext);
    }

    public async Task<UserPrivacySettingsDto> GetPrivacySettings(
        Guid targetUserId,
        Guid actorUserId,
        CancellationToken cancellationToken)
    {
        await EnsurePrivacySettingsAccess(targetUserId, actorUserId, cancellationToken);
        return ToPrivacyDto(await GetOrCreatePrivacySettings(targetUserId, cancellationToken));
    }

    public async Task<UserPrivacySettingsDto> UpdatePrivacySettings(
        Guid targetUserId,
        Guid actorUserId,
        UpdateUserPrivacySettingsRequest request,
        CancellationToken cancellationToken)
    {
        await EnsurePrivacySettingsAccess(targetUserId, actorUserId, cancellationToken);

        if (!IsValidVisibility(request.EmailVisibility) ||
            !IsValidVisibility(request.PhoneVisibility) ||
            !IsValidVisibility(request.BirthDateVisibility) ||
            !IsValidVisibility(request.PhysicalVisibility) ||
            !IsValidVisibility(request.HockeyProfileVisibility) ||
            !IsValidVisibility(request.SpbhlProfileVisibility))
        {
            throw new BusinessRuleException("Некорректный уровень видимости.");
        }

        var settings = await GetOrCreatePrivacySettings(targetUserId, cancellationToken);
        settings.EmailVisibility = request.EmailVisibility;
        settings.PhoneVisibility = request.PhoneVisibility;
        settings.BirthDateVisibility = request.BirthDateVisibility;
        settings.PhysicalVisibility = request.PhysicalVisibility;
        settings.HockeyProfileVisibility = request.HockeyProfileVisibility;
        settings.SpbhlProfileVisibility = request.SpbhlProfileVisibility;
        settings.UpdatedAt = _timeProvider.GetUtcNow().UtcDateTime;

        await _context.SaveChangesAsync(cancellationToken);
        return ToPrivacyDto(settings);
    }

    private async Task EnsurePrivacySettingsAccess(
        Guid targetUserId,
        Guid actorUserId,
        CancellationToken cancellationToken)
    {
        var userExists = await _context.Users
            .AsNoTracking()
            .AnyAsync(user => user.Id == targetUserId, cancellationToken);

        if (!userExists)
        {
            throw new NotFoundException("Пользователь не найден.");
        }

        if (actorUserId != targetUserId)
        {
            throw new UnauthorizedException("Недостаточно прав для доступа к настройкам приватности.");
        }
    }

    private async Task<UserPrivacySettings> GetOrCreatePrivacySettings(
        Guid userId,
        CancellationToken cancellationToken)
    {
        var settings = await _context.UserPrivacySettings
            .FirstOrDefaultAsync(value => value.UserId == userId, cancellationToken);
        if (settings != null)
        {
            return settings;
        }

        var now = _timeProvider.GetUtcNow().UtcDateTime;
        settings = new UserPrivacySettings
        {
            UserId = userId,
            CreatedAt = now,
            UpdatedAt = now,
        };

        await _context.UserPrivacySettings.AddAsync(settings, cancellationToken);
        await _context.SaveChangesAsync(cancellationToken);
        return settings;
    }

    private async Task<UserPrivacyViewerContext> BuildViewerContext(
        Guid targetUserId,
        Guid? viewerUserId,
        Guid? teamId,
        CancellationToken cancellationToken)
    {
        if (!viewerUserId.HasValue)
        {
            return new UserPrivacyViewerContext();
        }

        var viewerAppRole = await _context.Users
            .AsNoTracking()
            .Where(value => value.Id == viewerUserId.Value)
            .Select(value => (AppRole?)value.AppRole)
            .FirstOrDefaultAsync(cancellationToken);

        if (viewerAppRole == AppRole.SuperAdmin || viewerUserId.Value == targetUserId)
        {
            return new UserPrivacyViewerContext
            {
                IsOwner = viewerUserId.Value == targetUserId,
                IsSuperAdmin = viewerAppRole == AppRole.SuperAdmin,
                IsTeammate = true,
                IsTeamAdmin = true,
            };
        }

        var targetTeamIdsQuery = _context.TeamMemberships
            .AsNoTracking()
            .Where(value => value.UserId == targetUserId);

        if (teamId.HasValue)
        {
            targetTeamIdsQuery = targetTeamIdsQuery.Where(value => value.TeamId == teamId.Value);
        }

        var targetTeamIds = await targetTeamIdsQuery
            .Select(value => value.TeamId)
            .ToListAsync(cancellationToken);

        if (targetTeamIds.Count == 0)
        {
            return new UserPrivacyViewerContext();
        }

        var viewerMemberships = await _context.TeamMemberships
            .AsNoTracking()
            .Where(value =>
                value.UserId == viewerUserId.Value &&
                targetTeamIds.Contains(value.TeamId))
            .Select(value => value.Role)
            .ToListAsync(cancellationToken);

        return new UserPrivacyViewerContext
        {
            IsTeammate = viewerMemberships.Count > 0,
            IsTeamAdmin = viewerMemberships.Any(value =>
                value == TeamMemberRole.Owner || value == TeamMemberRole.Admin),
        };
    }

    private static UserProfileDto ToProfileDto(
        User user,
        UserPrivacySettings settings,
        UserPrivacyViewerContext viewerContext)
    {
        var canSeeEmail = CanSee(settings.EmailVisibility, viewerContext);
        var canSeePhone = CanSee(settings.PhoneVisibility, viewerContext);
        var canSeeBirthDate = CanSee(settings.BirthDateVisibility, viewerContext);
        var canSeePhysical = CanSee(settings.PhysicalVisibility, viewerContext);
        var canSeeHockeyProfile = CanSee(settings.HockeyProfileVisibility, viewerContext);
        var canSeeSpbhlProfile = CanSee(settings.SpbhlProfileVisibility, viewerContext);

        return new UserProfileDto
        {
            Id = user.Id,
            FirstName = user.FirstName,
            LastName = user.LastName,
            Email = canSeeEmail ? user.Email : null,
            EmailConfirmed = viewerContext.IsOwner || viewerContext.IsSuperAdmin
                ? user.EmailConfirmed
                : false,
            Phone = canSeePhone ? user.Phone : null,
            PhotoUrl = user.PhotoUrl,
            SpbhlPlayerId = canSeeSpbhlProfile ? user.SpbhlPlayerId : null,
            Role = user.Role,
            AppRole = viewerContext.IsSuperAdmin ? user.AppRole : AppRole.User,
            JerseyNumber = user.JerseyNumber,
            PrimaryPosition = canSeeHockeyProfile ? user.PrimaryPosition : null,
            Handedness = canSeeHockeyProfile ? user.Handedness : null,
            Height = canSeePhysical ? user.Height : null,
            Weight = canSeePhysical ? user.Weight : null,
            BirthDate = canSeeBirthDate ? user.BirthDate : null,
            CreatedAt = user.CreatedAt,
            UpdatedAt = user.UpdatedAt,
            FullName = user.FullName,
        };
    }

    private static bool CanSee(
        UserDataVisibility visibility,
        UserPrivacyViewerContext viewerContext)
    {
        if (viewerContext.IsOwner || viewerContext.IsSuperAdmin)
        {
            return true;
        }

        return visibility switch
        {
            UserDataVisibility.Everyone => true,
            UserDataVisibility.Teammates => viewerContext.IsTeammate,
            UserDataVisibility.TeamAdmins => viewerContext.IsTeamAdmin,
            _ => false,
        };
    }

    private static UserPrivacySettingsDto ToPrivacyDto(UserPrivacySettings settings) =>
        new()
        {
            UserId = settings.UserId,
            EmailVisibility = settings.EmailVisibility,
            PhoneVisibility = settings.PhoneVisibility,
            BirthDateVisibility = settings.BirthDateVisibility,
            PhysicalVisibility = settings.PhysicalVisibility,
            HockeyProfileVisibility = settings.HockeyProfileVisibility,
            SpbhlProfileVisibility = settings.SpbhlProfileVisibility,
        };

    private static bool IsValidVisibility(UserDataVisibility visibility) =>
        Enum.IsDefined(typeof(UserDataVisibility), visibility);

    private TimeZoneInfo ResolveBirthdayTimeZone()
    {
        var timeZoneId = _configuration["BIRTHDAY_TIMEZONE"] ?? "Europe/Moscow";
        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById(timeZoneId);
        }
        catch (TimeZoneNotFoundException)
        {
            return TimeZoneInfo.Utc;
        }
        catch (InvalidTimeZoneException)
        {
            return TimeZoneInfo.Utc;
        }
    }

    private static BirthdaysTodayResponse EmptyBirthdayResponse(DateTime today) =>
        new()
        {
            Date = today.ToString("yyyy-MM-dd"),
            Users = new List<BirthdayUserDto>(),
        };

    private static DateTime NormalizeToUtc(DateTime value) =>
        value.Kind switch
        {
            DateTimeKind.Utc => value,
            DateTimeKind.Local => value.ToUniversalTime(),
            _ => DateTime.SpecifyKind(value, DateTimeKind.Utc),
        };

    private sealed class UserPrivacyViewerContext
    {
        public bool IsOwner { get; init; }
        public bool IsSuperAdmin { get; init; }
        public bool IsTeammate { get; init; }
        public bool IsTeamAdmin { get; init; }
    }
}
