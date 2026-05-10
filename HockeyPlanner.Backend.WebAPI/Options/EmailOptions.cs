namespace HockeyPlanner.Backend.WebAPI.Options
{
    public sealed class EmailOptions
    {
        public string SmtpHost { get; set; } = string.Empty;
        public int SmtpPort { get; set; } = 587;
        public string SmtpUser { get; set; } = string.Empty;
        public string SmtpPassword { get; set; } = string.Empty;
        public bool EnableSsl { get; set; } = true;
        public string FromEmail { get; set; } = string.Empty;
        public string FromName { get; set; } = "Hockey Planner";
        public string FrontendBaseUrl { get; set; } = "http://localhost:3000";
    }
}
