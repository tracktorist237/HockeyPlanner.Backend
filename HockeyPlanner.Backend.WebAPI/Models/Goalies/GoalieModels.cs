using HockeyPlanner.Backend.Core.Enums;

namespace HockeyPlanner.Backend.WebAPI.Models.Goalies
{
    public class UpsertGoalieRequestRequest
    {
        public int NeededCount { get; set; }
        public GoalieRequestVisibility Visibility { get; set; }
        public GoalieRequestResponseMode ResponseMode { get; set; }
        public string? PriceText { get; set; }
        public string? Description { get; set; }
    }

    public class CreateGoalieApplicationRequest
    {
        public string? Message { get; set; }
    }

    public class ProposeGoalieRequest
    {
        public Guid GoalieUserId { get; set; }
        public string? Message { get; set; }
    }

    public class UpdateGoalieApplicationStatusRequest
    {
        public GoalieApplicationStatus Status { get; set; }
    }

    public class GoalieEventConflictDto
    {
        public Guid EventId { get; set; }
        public string Title { get; set; } = string.Empty;
        public DateTime StartTime { get; set; }
    }

    public class GoalieUserDto
    {
        public Guid UserId { get; set; }
        public string FirstName { get; set; } = string.Empty;
        public string LastName { get; set; } = string.Empty;
        public int? JerseyNumber { get; set; }
        public string? PhotoUrl { get; set; }
        public GoalieEventConflictDto? Conflict { get; set; }
    }

    public class GoalieApplicationDto : GoalieUserDto
    {
        public Guid Id { get; set; }
        public GoalieApplicationStatus Status { get; set; }
        public GoalieApplicationSource Source { get; set; }
        public string? Message { get; set; }
        public DateTime CreatedAt { get; set; }
        public DateTime? UpdatedAt { get; set; }
    }

    public class GoalieRequestDto
    {
        public Guid Id { get; set; }
        public Guid EventId { get; set; }
        public Guid? TeamId { get; set; }
        public int NeededCount { get; set; }
        public GoalieRequestVisibility Visibility { get; set; }
        public GoalieRequestResponseMode ResponseMode { get; set; }
        public GoalieRequestStatus Status { get; set; }
        public string? PriceText { get; set; }
        public string? Description { get; set; }
        public int ConfirmedCount { get; set; }
        public DateTime CreatedAt { get; set; }
        public DateTime? UpdatedAt { get; set; }
        public List<GoalieApplicationDto> Applications { get; set; } = new();
    }

    public class EventGoaliesDto
    {
        public bool IsGoalie { get; set; }
        public bool IsTeamMember { get; set; }
        public bool CanManage { get; set; }
        public bool CanApply { get; set; }
        public GoalieEventConflictDto? CurrentUserConflict { get; set; }
        public GoalieApplicationDto? MyApplication { get; set; }
        public GoalieRequestDto? Request { get; set; }
        public List<GoalieUserDto> AvailableGoalies { get; set; } = new();
        public List<GoalieRequestDto> PreviousRequests { get; set; } = new();
    }
}
