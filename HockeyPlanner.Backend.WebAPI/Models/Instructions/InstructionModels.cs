namespace HockeyPlanner.Backend.WebAPI.Models.Instructions
{
    public class InstructionListItemDto
    {
        public Guid Id { get; set; }
        public string Slug { get; set; } = string.Empty;
        public string Title { get; set; } = string.Empty;
        public string? Summary { get; set; }
        public string? ImageUrl { get; set; }
        public int SortOrder { get; set; }
        public DateTime? PublishedAt { get; set; }
    }

    public sealed class InstructionArticleDto : InstructionListItemDto
    {
        public string Content { get; set; } = string.Empty;
        public bool IsPublished { get; set; }
        public DateTime CreatedAt { get; set; }
        public DateTime? UpdatedAt { get; set; }
        public Guid? CreatedByUserId { get; set; }
    }

    public sealed class CreateUpdateInstructionArticleRequest
    {
        public string Slug { get; set; } = string.Empty;
        public string Title { get; set; } = string.Empty;
        public string? Summary { get; set; }
        public string Content { get; set; } = string.Empty;
        public string? ImageUrl { get; set; }
        public bool IsPublished { get; set; }
        public int SortOrder { get; set; }
    }

    public sealed class UploadInstructionImageResponse
    {
        public string ImageUrl { get; set; } = string.Empty;
    }
}
