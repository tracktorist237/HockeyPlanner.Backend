using HockeyPlanner.Backend.Core.Enums;

namespace HockeyPlanner.Backend.WebAPI.Models.Teams
{
    public class CreateTeamRequest
    {
        public string Name { get; set; } = string.Empty;
        public string? Description { get; set; }
        public TeamVisibility Visibility { get; set; } = TeamVisibility.Private;
        public string? AvatarUrl { get; set; }
        public string? CoverImageUrl { get; set; }
        public List<TeamContactItemDto> Phones { get; set; } = new();
        public List<TeamContactItemDto> Links { get; set; } = new();
        public List<TeamContactItemDto> Addresses { get; set; } = new();
    }

    public class UpdateTeamRequest
    {
        public string Name { get; set; } = string.Empty;
        public string? Description { get; set; }
        public TeamVisibility Visibility { get; set; } = TeamVisibility.Private;
        public string? AvatarUrl { get; set; }
        public string? CoverImageUrl { get; set; }
        public List<TeamContactItemDto> Phones { get; set; } = new();
        public List<TeamContactItemDto> Links { get; set; } = new();
        public List<TeamContactItemDto> Addresses { get; set; } = new();
    }

    public class TeamContactItemDto
    {
        public string Title { get; set; } = string.Empty;
        public string Value { get; set; } = string.Empty;
    }

    public class JoinTeamByCodeRequest
    {
        public string Code { get; set; } = string.Empty;
    }

    public class TeamDto
    {
        public Guid Id { get; set; }
        public string Name { get; set; } = string.Empty;
        public string? Description { get; set; }
        public string? AvatarUrl { get; set; }
        public string? CoverImageUrl { get; set; }
        public IReadOnlyCollection<TeamContactItemDto> Phones { get; set; } = Array.Empty<TeamContactItemDto>();
        public IReadOnlyCollection<TeamContactItemDto> Links { get; set; } = Array.Empty<TeamContactItemDto>();
        public IReadOnlyCollection<TeamContactItemDto> Addresses { get; set; } = Array.Empty<TeamContactItemDto>();
        public TeamVisibility Visibility { get; set; }
        public string InviteCode { get; set; } = string.Empty;
        public Guid CreatedByUserId { get; set; }
        public int MembersCount { get; set; }
        public TeamMemberRole? MyRole { get; set; }
        public string? MyBadgeTitle { get; set; }
    }

    public class TeamMemberDto
    {
        public Guid UserId { get; set; }
        public string FirstName { get; set; } = string.Empty;
        public string LastName { get; set; } = string.Empty;
        public int? JerseyNumber { get; set; }
        public string? PhotoUrl { get; set; }
        public TeamMemberRole Role { get; set; }
        public string? BadgeTitle { get; set; }
    }

    public class UpdateTeamMemberRequest
    {
        public TeamMemberRole? Role { get; set; }
        public string? BadgeTitle { get; set; }
    }

    public class TeamNewsDto
    {
        public Guid Id { get; set; }
        public Guid TeamId { get; set; }
        public string Title { get; set; } = string.Empty;
        public string Body { get; set; } = string.Empty;
        public Guid AuthorUserId { get; set; }
        public string AuthorName { get; set; } = string.Empty;
        public DateTime CreatedAt { get; set; }
    }

    public class CreateTeamNewsRequest
    {
        public string Title { get; set; } = string.Empty;
        public string Body { get; set; } = string.Empty;
    }
}
