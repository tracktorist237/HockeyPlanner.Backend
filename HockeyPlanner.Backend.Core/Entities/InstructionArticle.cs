using HockeyPlanner.Backend.Core.Entities.Base;

namespace HockeyPlanner.Backend.Core.Entities
{
    public class InstructionArticle : Entity
    {
        public string Slug { get; set; } = string.Empty;
        public string Title { get; set; } = string.Empty;
        public string? Summary { get; set; }
        public string Content { get; set; } = string.Empty;
        public string? ImageUrl { get; set; }
        public bool IsPublished { get; set; }
        public int SortOrder { get; set; }
        public DateTime? PublishedAt { get; set; }
        public Guid? CreatedByUserId { get; set; }
        public User? CreatedByUser { get; set; }
    }
}
