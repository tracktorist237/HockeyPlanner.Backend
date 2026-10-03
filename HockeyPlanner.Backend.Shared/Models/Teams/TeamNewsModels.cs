namespace HockeyPlanner.Backend.Shared.Models.Teams
{
    public class TeamNewsDto
    {
        public Guid Id { get; set; }
        public Guid TeamId { get; set; }
        public string TeamName { get; set; } = string.Empty;
        public string Title { get; set; } = string.Empty;
        public string Body { get; set; } = string.Empty;
        public string? ImageUrl { get; set; }
        public Guid AuthorUserId { get; set; }
        public string AuthorName { get; set; } = string.Empty;
        public DateTime CreatedAt { get; set; }
        public DateTime? UpdatedAt { get; set; }
        public bool CanManage { get; set; }
    }

    public class CreateTeamNewsRequest
    {
        public string Title { get; set; } = string.Empty;
        public string Body { get; set; } = string.Empty;
        public string? ImageUrl { get; set; }
        public bool SendNotification { get; set; }
    }

    public class UpdateTeamNewsRequest
    {
        public string Title { get; set; } = string.Empty;
        public string Body { get; set; } = string.Empty;
        public string? ImageUrl { get; set; }
    }

}
