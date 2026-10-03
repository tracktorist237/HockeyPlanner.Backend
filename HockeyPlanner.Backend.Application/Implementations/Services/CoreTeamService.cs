using HockeyPlanner.Backend.Application.Abstractions.Services;
using HockeyPlanner.Backend.Application.Policies;
using HockeyPlanner.Backend.Core.Entities;
using HockeyPlanner.Backend.Core.Enums;
using HockeyPlanner.Backend.Core.Exceptions;
using HockeyPlanner.Backend.Infrastructure.Data;
using HockeyPlanner.Backend.Shared.Models.Teams;
using Microsoft.EntityFrameworkCore;
using System.Text.Json;

namespace HockeyPlanner.Backend.Application.Implementations.Services;

internal sealed class CoreTeamService(AppDbContext context, TimeProvider timeProvider) : ICoreTeamService
{
    private readonly AppDbContext _context = context;
    private readonly TimeProvider _timeProvider = timeProvider;

    public async Task<IReadOnlyCollection<TeamDto>> GetMyTeams(Guid actorUserId, CancellationToken cancellationToken)
    {
        var userExists = await _context.Users.AsNoTracking().AnyAsync(user => user.Id == actorUserId, cancellationToken);
        if (!userExists)
        {
            throw new NotFoundException("Пользователь не найден.");
        }

        var teams = await _context.TeamMemberships
            .AsNoTracking()
            .Where(value => value.UserId == actorUserId)
            .OrderBy(value => value.Team.Name)
            .Select(value => new TeamDto
            {
                Id = value.Team.Id,
                Name = value.Team.Name,
                Description = value.Team.Description,
                AvatarUrl = value.Team.AvatarUrl,
                CoverImageUrl = value.Team.CoverImageUrl,
                Visibility = value.Team.Visibility,
                InviteCode = CoreTeamRolePolicy.CanManage(value.Role)
                    ? value.Team.InviteCode
                    : string.Empty,
                CreatedByUserId = value.Team.CreatedByUserId,
                MembersCount = value.Team.Memberships.Count,
                MyRole = value.Role,
                MyBadgeTitle = value.BadgeTitle,
                MyTeamJerseyNumber = value.TeamJerseyNumber,
                AllowDuplicateJerseyNumbers = value.Team.AllowDuplicateJerseyNumbers,
                BlockedJerseyNumbers = DeserializeJerseyNumbers(value.Team.BlockedJerseyNumbersJson)
            })
            .ToListAsync(cancellationToken);

        return teams;
    }

    public async Task<IReadOnlyCollection<TeamDto>> GetPublicTeams(CancellationToken cancellationToken)
    {
        var teams = await _context.Teams
            .AsNoTracking()
            .Where(team => team.Visibility == TeamVisibility.Public)
            .OrderBy(team => team.Name)
            .Select(team => new TeamDto
            {
                Id = team.Id,
                Name = team.Name,
                Description = team.Description,
                AvatarUrl = team.AvatarUrl,
                CoverImageUrl = team.CoverImageUrl,
                Visibility = team.Visibility,
                InviteCode = string.Empty,
                CreatedByUserId = team.CreatedByUserId,
                MembersCount = team.Memberships.Count,
                AllowDuplicateJerseyNumbers = team.AllowDuplicateJerseyNumbers,
                BlockedJerseyNumbers = DeserializeJerseyNumbers(team.BlockedJerseyNumbersJson)
            })
            .ToListAsync(cancellationToken);

        return teams;
    }

    public async Task<TeamDto> GetTeam(Guid id, Guid? viewerUserId, CancellationToken cancellationToken)
    {
        var team = await _context.Teams
            .AsNoTracking()
            .Include(value => value.Memberships)
            .Where(value => value.Id == id)
            .FirstOrDefaultAsync(cancellationToken);

        if (team == null)
        {
            throw new NotFoundException("Команда не найдена.");
        }

        EnsureVisible(team, viewerUserId);

        var membership = viewerUserId.HasValue
            ? team.Memberships.FirstOrDefault(member => member.UserId == viewerUserId)
            : null;
        var canSeeInvite = CoreTeamRolePolicy.CanManage(membership?.Role);

        return ToDto(team, membership?.Role, membership?.BadgeTitle, canSeeInvite ? team.InviteCode : string.Empty, myTeamJerseyNumber: membership?.TeamJerseyNumber);
    }

    public async Task<IReadOnlyCollection<TeamMemberDto>> GetTeamMembers(Guid id, Guid? viewerUserId, CancellationToken cancellationToken)
    {
        var team = await _context.Teams.AsNoTracking()
            .Include(value => value.Memberships)
            .FirstOrDefaultAsync(value => value.Id == id, cancellationToken);
        if (team == null)
        {
            throw new NotFoundException("Команда не найдена.");
        }

        EnsureVisible(team, viewerUserId);

        var members = await _context.TeamMemberships
            .AsNoTracking()
            .Where(value => value.TeamId == id)
            .OrderBy(value => value.Role)
            .ThenBy(value => value.User.LastName)
            .ThenBy(value => value.User.FirstName)
            .Select(value => new TeamMemberDto
            {
                UserId = value.UserId,
                FirstName = value.User.FirstName,
                LastName = value.User.LastName,
                JerseyNumber = value.User.JerseyNumber,
                PhotoUrl = value.User.PhotoUrl,
                Role = value.Role,
                BadgeTitle = value.BadgeTitle,
                TeamJerseyNumber = value.TeamJerseyNumber
            })
            .ToListAsync(cancellationToken);

        return members;
    }

    public async Task<TeamDto> CreateTeam(Guid actorUserId, CreateTeamRequest request, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.Name))
        {
            throw new BusinessRuleException("Название команды обязательно.");
        }

        var userExists = await _context.Users.AsNoTracking().AnyAsync(user => user.Id == actorUserId, cancellationToken);
        if (!userExists)
        {
            throw new NotFoundException("Пользователь не найден.");
        }

        var normalizedName = NormalizeName(request.Name);

        var duplicateExists = await _context.Teams
            .AsNoTracking()
            .AnyAsync(team => team.Name.ToLower() == normalizedName.ToLower(), cancellationToken);

        if (duplicateExists)
        {
            throw new ConflictException("Команда с таким названием уже существует.");
        }

        var inviteCode = await GenerateUniqueInviteCode(cancellationToken);
        if (request.BlockedJerseyNumbers.Any(value => value < 0 || value > 99))
        {
            throw new BusinessRuleException("Командные номера должны быть от 0 до 99.");
        }

        var team = new Team
        {
            Name = normalizedName,
            Description = NormalizeDescription(request.Description),
            AvatarUrl = NormalizeUrl(request.AvatarUrl),
            CoverImageUrl = NormalizeUrl(request.CoverImageUrl),
            PhoneContactsJson = SerializeContacts(request.Phones),
            LinkContactsJson = SerializeContacts(request.Links),
            AddressContactsJson = SerializeContacts(request.Addresses),
            Visibility = request.Visibility,
            AllowDuplicateJerseyNumbers = request.AllowDuplicateJerseyNumbers,
            BlockedJerseyNumbersJson = SerializeJerseyNumbers(NormalizeJerseyNumbers(request.BlockedJerseyNumbers)),
            InviteCode = inviteCode,
            CreatedByUserId = actorUserId,
            CreatedAt = _timeProvider.GetUtcNow().UtcDateTime,
            UpdatedAt = _timeProvider.GetUtcNow().UtcDateTime
        };

        var ownerMembership = new TeamMembership
        {
            Team = team,
            UserId = actorUserId,
            Role = TeamMemberRole.Owner,
            CreatedAt = _timeProvider.GetUtcNow().UtcDateTime,
            UpdatedAt = _timeProvider.GetUtcNow().UtcDateTime
        };

        await _context.Teams.AddAsync(team, cancellationToken);
        await _context.TeamMemberships.AddAsync(ownerMembership, cancellationToken);
        await _context.SaveChangesAsync(cancellationToken);

        var dto = ToDto(team, TeamMemberRole.Owner, ownerMembership.BadgeTitle, team.InviteCode, 1, ownerMembership.TeamJerseyNumber);

        return dto;
    }

    public async Task<TeamDto> UpdateTeam(Guid id, Guid actorUserId, UpdateTeamRequest request, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.Name))
        {
            throw new BusinessRuleException("Название команды обязательно.");
        }

        var team = await _context.Teams
            .Include(value => value.Memberships)
            .FirstOrDefaultAsync(value => value.Id == id, cancellationToken);

        if (team == null)
        {
            throw new NotFoundException("Команда не найдена.");
        }

        var actorMembership = team.Memberships.FirstOrDefault(value => value.UserId == actorUserId);
        CoreTeamRolePolicy.EnsureCanManage(actorMembership?.Role);

        var normalizedName = NormalizeName(request.Name);
        var duplicateExists = await _context.Teams
            .AsNoTracking()
            .AnyAsync(value => value.Id != id && value.Name.ToLower() == normalizedName.ToLower(), cancellationToken);

        if (duplicateExists)
        {
            throw new ConflictException("Команда с таким названием уже существует.");
        }

        team.Name = normalizedName;
        team.Description = NormalizeDescription(request.Description);
        team.AvatarUrl = NormalizeUrl(request.AvatarUrl);
        team.CoverImageUrl = NormalizeUrl(request.CoverImageUrl);
        team.PhoneContactsJson = SerializeContacts(request.Phones);
        team.LinkContactsJson = SerializeContacts(request.Links);
        team.AddressContactsJson = SerializeContacts(request.Addresses);
        team.Visibility = request.Visibility;
        var blockedNumbers = NormalizeJerseyNumbers(request.BlockedJerseyNumbers);
        var invalidBlockedNumber = request.BlockedJerseyNumbers.FirstOrDefault(value => value < 0 || value > 99);
        if (request.BlockedJerseyNumbers.Any(value => value < 0 || value > 99))
        {
            throw new BusinessRuleException($"Недопустимый номер: {invalidBlockedNumber}. Используйте числа от 0 до 99.");
        }

        if (!request.AllowDuplicateJerseyNumbers)
        {
            var duplicateNumber = team.Memberships
                .Where(value => value.TeamJerseyNumber.HasValue)
                .GroupBy(value => value.TeamJerseyNumber!.Value)
                .FirstOrDefault(group => group.Count() > 1)?.Key;
            if (duplicateNumber.HasValue)
            {
                throw new ConflictException($"Номер {duplicateNumber.Value} уже используется несколькими участниками. Сначала измените их номера.");
            }
        }

        var blockedAssignedNumber = team.Memberships
            .Where(value => value.TeamJerseyNumber.HasValue && blockedNumbers.Contains(value.TeamJerseyNumber.Value))
            .Select(value => value.TeamJerseyNumber)
            .FirstOrDefault();
        if (blockedAssignedNumber.HasValue)
        {
            throw new ConflictException($"Номер {blockedAssignedNumber.Value} уже назначен участнику. Сначала измените его номер.");
        }

        team.AllowDuplicateJerseyNumbers = request.AllowDuplicateJerseyNumbers;
        team.BlockedJerseyNumbersJson = SerializeJerseyNumbers(blockedNumbers);
        team.UpdatedAt = _timeProvider.GetUtcNow().UtcDateTime;

        await _context.SaveChangesAsync(cancellationToken);

        return ToDto(team, actorMembership!.Role, actorMembership.BadgeTitle, team.InviteCode, myTeamJerseyNumber: actorMembership.TeamJerseyNumber);
    }

    public async Task<TeamMemberDto> UpdateTeamMember(Guid id, Guid userId, Guid actorUserId, UpdateTeamMemberRequest request, CancellationToken cancellationToken)
    {
        var actorMembership = await _context.TeamMemberships
            .AsNoTracking()
            .FirstOrDefaultAsync(value => value.TeamId == id && value.UserId == actorUserId, cancellationToken);

        CoreTeamRolePolicy.EnsureCanManage(actorMembership?.Role);

        var targetMembership = await _context.TeamMemberships
            .Include(value => value.User)
            .FirstOrDefaultAsync(value => value.TeamId == id && value.UserId == userId, cancellationToken);

        if (targetMembership == null)
        {
            throw new NotFoundException("Участник команды не найден.");
        }

        CoreTeamRolePolicy.EnsureCanUpdateMember(actorMembership!.Role, targetMembership.Role, request.Role);
        if (request.Role.HasValue) targetMembership.Role = request.Role.Value;

        targetMembership.BadgeTitle = NormalizeBadgeTitle(request.BadgeTitle);
        var numberError = await ValidateTeamJerseyNumber(id, request.TeamJerseyNumber, targetMembership.UserId, cancellationToken);
        if (numberError != null)
        {
            throw new ConflictException(numberError);
        }
        targetMembership.TeamJerseyNumber = request.TeamJerseyNumber;
        targetMembership.UpdatedAt = _timeProvider.GetUtcNow().UtcDateTime;

        await _context.SaveChangesAsync(cancellationToken);

        return new TeamMemberDto
        {
            UserId = targetMembership.UserId,
            FirstName = targetMembership.User.FirstName,
            LastName = targetMembership.User.LastName,
            JerseyNumber = targetMembership.User.JerseyNumber,
            PhotoUrl = targetMembership.User.PhotoUrl,
            Role = targetMembership.Role,
            BadgeTitle = targetMembership.BadgeTitle,
            TeamJerseyNumber = targetMembership.TeamJerseyNumber
        };
    }

    public async Task RemoveTeamMember(Guid id, Guid userId, Guid actorUserId, CancellationToken cancellationToken)
    {
        if (userId == actorUserId)
        {
            throw new BusinessRuleException("Для выхода из команды используйте действие покинуть команду.");
        }

        var actorMembership = await _context.TeamMemberships
            .AsNoTracking()
            .FirstOrDefaultAsync(value => value.TeamId == id && value.UserId == actorUserId, cancellationToken);

        CoreTeamRolePolicy.EnsureCanManage(actorMembership?.Role);

        var targetMembership = await _context.TeamMemberships
            .FirstOrDefaultAsync(value => value.TeamId == id && value.UserId == userId, cancellationToken);

        if (targetMembership == null)
        {
            throw new NotFoundException("Участник команды не найден.");
        }

        CoreTeamRolePolicy.EnsureCanRemove(actorMembership!.Role, targetMembership.Role);

        _context.TeamMemberships.Remove(targetMembership);
        await _context.SaveChangesAsync(cancellationToken);

        return;
    }

    public async Task<TeamDto> JoinByCode(Guid actorUserId, JoinTeamByCodeRequest request, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.Code))
        {
            throw new BusinessRuleException("Код приглашения обязателен.");
        }

        var normalizedCode = request.Code.Trim().ToUpperInvariant();

        var team = await _context.Teams
            .Include(value => value.Memberships)
            .FirstOrDefaultAsync(value => value.InviteCode == normalizedCode, cancellationToken);

        if (team == null)
        {
            throw new NotFoundException("Команда с таким кодом не найдена.");
        }

        return await JoinTeamInternal(team, actorUserId, request.TeamJerseyNumber, cancellationToken);
    }

    public async Task<TeamDto> JoinPublic(Guid id, Guid actorUserId, int? teamJerseyNumber, CancellationToken cancellationToken)
    {
        var team = await _context.Teams
            .Include(value => value.Memberships)
            .FirstOrDefaultAsync(value => value.Id == id, cancellationToken);

        if (team == null)
        {
            throw new NotFoundException("Команда не найдена.");
        }

        if (team.Visibility != TeamVisibility.Public)
        {
            throw new BusinessRuleException("В приватную команду можно вступить только по коду.");
        }

        return await JoinTeamInternal(team, actorUserId, teamJerseyNumber, cancellationToken);
    }

    public async Task LeaveTeam(Guid id, Guid actorUserId, CancellationToken cancellationToken)
    {
        var membership = await _context.TeamMemberships
            .FirstOrDefaultAsync(value => value.TeamId == id && value.UserId == actorUserId, cancellationToken);

        if (membership == null)
        {
            throw new NotFoundException("Вы не состоите в этой команде.");
        }

        CoreTeamRolePolicy.EnsureCanLeave(membership.Role);

        _context.TeamMemberships.Remove(membership);
        await _context.SaveChangesAsync(cancellationToken);

        return;
    }

    public async Task<TeamDto> UpdateMyTeamJerseyNumber(Guid id, Guid actorUserId, UpdateMyTeamJerseyNumberRequest request, CancellationToken cancellationToken)
    {
        var membership = await _context.TeamMemberships
            .Include(value => value.Team)
            .ThenInclude(value => value.Memberships)
            .FirstOrDefaultAsync(value => value.TeamId == id && value.UserId == actorUserId, cancellationToken);
        if (membership == null)
        {
            throw new NotFoundException("Вы не состоите в этой команде.");
        }

        var numberError = await ValidateTeamJerseyNumber(id, request.TeamJerseyNumber, actorUserId, cancellationToken);
        if (numberError != null)
        {
            throw new ConflictException(numberError);
        }

        membership.TeamJerseyNumber = request.TeamJerseyNumber;
        membership.UpdatedAt = _timeProvider.GetUtcNow().UtcDateTime;
        await _context.SaveChangesAsync(cancellationToken);

        var canSeeInvite = CoreTeamRolePolicy.CanManage(membership.Role);
        return ToDto(membership.Team, membership.Role, membership.BadgeTitle, canSeeInvite ? membership.Team.InviteCode : string.Empty, myTeamJerseyNumber: membership.TeamJerseyNumber);
    }

    private async Task<TeamDto> JoinTeamInternal(Team team, Guid actorUserId, int? teamJerseyNumber, CancellationToken cancellationToken)
    {
        var userExists = await _context.Users.AsNoTracking().AnyAsync(user => user.Id == actorUserId, cancellationToken);
        if (!userExists)
        {
            throw new NotFoundException("Пользователь не найден.");
        }

        var alreadyMember = team.Memberships.Any(value => value.UserId == actorUserId);
        if (alreadyMember)
        {
            return new TeamDto
            {
                Id = team.Id,
                Name = team.Name,
                Description = team.Description,
                AvatarUrl = team.AvatarUrl,
                CoverImageUrl = team.CoverImageUrl,
                Visibility = team.Visibility,
                InviteCode = string.Empty,
                CreatedByUserId = team.CreatedByUserId,
                MembersCount = team.Memberships.Count,
                MyRole = team.Memberships.First(value => value.UserId == actorUserId).Role,
                MyBadgeTitle = team.Memberships.First(value => value.UserId == actorUserId).BadgeTitle,
                MyTeamJerseyNumber = team.Memberships.First(value => value.UserId == actorUserId).TeamJerseyNumber,
                AllowDuplicateJerseyNumbers = team.AllowDuplicateJerseyNumbers,
                BlockedJerseyNumbers = DeserializeJerseyNumbers(team.BlockedJerseyNumbersJson)
            };
        }

        var numberError = await ValidateTeamJerseyNumber(team.Id, teamJerseyNumber, actorUserId, cancellationToken);
        if (numberError != null)
        {
            throw new ConflictException(numberError);
        }

        var membership = new TeamMembership
        {
            TeamId = team.Id,
            UserId = actorUserId,
            Role = TeamMemberRole.Member,
            TeamJerseyNumber = teamJerseyNumber,
            CreatedAt = _timeProvider.GetUtcNow().UtcDateTime,
            UpdatedAt = _timeProvider.GetUtcNow().UtcDateTime
        };

        await _context.TeamMemberships.AddAsync(membership, cancellationToken);
        await _context.SaveChangesAsync(cancellationToken);

        return new TeamDto
        {
            Id = team.Id,
            Name = team.Name,
            Description = team.Description,
            AvatarUrl = team.AvatarUrl,
            CoverImageUrl = team.CoverImageUrl,
            Visibility = team.Visibility,
            InviteCode = string.Empty,
            CreatedByUserId = team.CreatedByUserId,
            MembersCount = team.Memberships.Count + 1,
            MyRole = membership.Role,
            MyBadgeTitle = membership.BadgeTitle,
            MyTeamJerseyNumber = membership.TeamJerseyNumber,
            AllowDuplicateJerseyNumbers = team.AllowDuplicateJerseyNumbers,
            BlockedJerseyNumbers = DeserializeJerseyNumbers(team.BlockedJerseyNumbersJson)
        };
    }

    private async Task<string> GenerateUniqueInviteCode(CancellationToken cancellationToken)
    {
        while (true)
        {
            var code = Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();
            var exists = await _context.Teams.AsNoTracking().AnyAsync(value => value.InviteCode == code, cancellationToken);
            if (!exists)
            {
                return code;
            }
        }
    }

    private static string NormalizeName(string value)
    {
        var parts = value
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return string.Join(" ", parts);
    }

    private static string? NormalizeDescription(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var normalized = NormalizeName(value);
        return normalized.Length > 1000 ? normalized[..1000] : normalized;
    }

    private static string? NormalizeBadgeTitle(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var normalized = NormalizeName(value);
        return normalized.Length > 32 ? normalized[..32] : normalized;
    }

    private static string? NormalizeUrl(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var normalized = value.Trim();
        return normalized.Length > 500 ? normalized[..500] : normalized;
    }

    private static TeamDto ToDto(
        Team team,
        TeamMemberRole? myRole,
        string? myBadgeTitle,
        string inviteCode,
        int? membersCount = null,
        int? myTeamJerseyNumber = null)
    {
        return new TeamDto
        {
            Id = team.Id,
            Name = team.Name,
            Description = team.Description,
            AvatarUrl = team.AvatarUrl,
            CoverImageUrl = team.CoverImageUrl,
            Phones = DeserializeContacts(team.PhoneContactsJson),
            Links = DeserializeContacts(team.LinkContactsJson),
            Addresses = DeserializeContacts(team.AddressContactsJson),
            Visibility = team.Visibility,
            InviteCode = inviteCode,
            CreatedByUserId = team.CreatedByUserId,
            MembersCount = membersCount ?? team.Memberships.Count,
            MyRole = myRole,
            MyBadgeTitle = myBadgeTitle,
            MyTeamJerseyNumber = myTeamJerseyNumber,
            AllowDuplicateJerseyNumbers = team.AllowDuplicateJerseyNumbers,
            BlockedJerseyNumbers = DeserializeJerseyNumbers(team.BlockedJerseyNumbersJson)
        };
    }

    private async Task<string?> ValidateTeamJerseyNumber(Guid teamId, int? number, Guid excludedUserId, CancellationToken cancellationToken)
    {
        var team = await _context.Teams.AsNoTracking().FirstOrDefaultAsync(value => value.Id == teamId, cancellationToken);
        if (team == null)
        {
            return "Команда не найдена.";
        }

        var numberRequired = !team.AllowDuplicateJerseyNumbers || DeserializeJerseyNumbers(team.BlockedJerseyNumbersJson).Count > 0;
        if (!number.HasValue)
        {
            return numberRequired ? "Укажите внутрикомандный номер." : null;
        }

        if (number.Value < 0 || number.Value > 99)
        {
            return "Внутрикомандный номер должен быть от 0 до 99.";
        }

        if (DeserializeJerseyNumbers(team.BlockedJerseyNumbersJson).Contains(number.Value))
        {
            return $"Номер {number.Value} запрещён в этой команде.";
        }

        if (!team.AllowDuplicateJerseyNumbers && await _context.TeamMemberships.AsNoTracking().AnyAsync(value =>
                value.TeamId == teamId && value.UserId != excludedUserId && value.TeamJerseyNumber == number.Value, cancellationToken))
        {
            return $"Номер {number.Value} уже занят в этой команде.";
        }

        return null;
    }

    private static List<int> NormalizeJerseyNumbers(IEnumerable<int>? values) =>
        (values ?? Array.Empty<int>()).Where(value => value >= 0 && value <= 99).Distinct().OrderBy(value => value).ToList();

    private static string? SerializeJerseyNumbers(IEnumerable<int>? values)
    {
        var normalized = NormalizeJerseyNumbers(values);
        return normalized.Count == 0 ? null : JsonSerializer.Serialize(normalized);
    }

    private static IReadOnlyCollection<int> DeserializeJerseyNumbers(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return Array.Empty<int>();
        try { return NormalizeJerseyNumbers(JsonSerializer.Deserialize<List<int>>(value)); }
        catch (JsonException) { return Array.Empty<int>(); }
    }

    private static string? SerializeContacts(IEnumerable<TeamContactItemDto>? contacts)
    {
        var normalized = NormalizeContacts(contacts).ToList();
        return normalized.Count == 0 ? null : JsonSerializer.Serialize(normalized);
    }

    private static IReadOnlyCollection<TeamContactItemDto> DeserializeContacts(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return Array.Empty<TeamContactItemDto>();
        }

        try
        {
            return NormalizeContacts(JsonSerializer.Deserialize<List<TeamContactItemDto>>(value)).ToList();
        }
        catch (JsonException)
        {
            return Array.Empty<TeamContactItemDto>();
        }
    }

    private static IEnumerable<TeamContactItemDto> NormalizeContacts(IEnumerable<TeamContactItemDto>? contacts)
    {
        return (contacts ?? Array.Empty<TeamContactItemDto>())
            .Select(contact => new TeamContactItemDto
            {
                Title = NormalizeName(contact.Title ?? string.Empty),
                Value = (contact.Value ?? string.Empty).Trim()
            })
            .Where(contact => !string.IsNullOrWhiteSpace(contact.Title) && !string.IsNullOrWhiteSpace(contact.Value))
            .Take(10)
            .Select(contact => new TeamContactItemDto
            {
                Title = contact.Title.Length > 80 ? contact.Title[..80] : contact.Title,
                Value = contact.Value.Length > 500 ? contact.Value[..500] : contact.Value
            });
    }

    private static void EnsureVisible(Team team, Guid? viewerUserId)
    {
        if (team.Visibility == TeamVisibility.Public) return;
        if (!viewerUserId.HasValue) throw new UnauthorizedException("Необходима авторизация");
        if (!team.Memberships.Any(member => member.UserId == viewerUserId))
            throw new UnauthorizedException("Недостаточно прав");
    }
}
