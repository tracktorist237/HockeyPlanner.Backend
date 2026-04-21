using HockeyPlanner.Backend.Core.Enums;

namespace HockeyPlanner.Backend.WebAPI.Models.Teams
{
    public class CreateTeamRequest
    {
        public string Name { get; set; } = string.Empty;
        public string? Description { get; set; }
        public TeamVisibility Visibility { get; set; } = TeamVisibility.Private;
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
        public TeamVisibility Visibility { get; set; }
        public string InviteCode { get; set; } = string.Empty;
        public Guid CreatedByUserId { get; set; }
        public int MembersCount { get; set; }
    }

    public class TeamMemberDto
    {
        public Guid UserId { get; set; }
        public string FirstName { get; set; } = string.Empty;
        public string LastName { get; set; } = string.Empty;
        public int? JerseyNumber { get; set; }
        public TeamMemberRole Role { get; set; }
    }
}
