namespace HockeyPlanner.Backend.WebAPI.Models.ReleaseNotices
{
    public sealed class PublicReleaseNoticeDto
    {
        public Guid Id { get; set; }
        public string Version { get; set; } = string.Empty;
        public string Title { get; set; } = string.Empty;
        public string Body { get; set; } = string.Empty;
        public DateTime? PublishedAt { get; set; }
    }
}
