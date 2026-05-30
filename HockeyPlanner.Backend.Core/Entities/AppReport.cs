using HockeyPlanner.Backend.Core.Entities.Base;
using HockeyPlanner.Backend.Core.Enums;

namespace HockeyPlanner.Backend.Core.Entities
{
    public class AppReport : Entity
    {
        public Guid? UserId { get; set; }
        public User? User { get; set; }

        public AppReportType Type { get; set; }
        public AppReportStatus Status { get; set; } = AppReportStatus.New;
        public AppReportSeverity Severity { get; set; } = AppReportSeverity.Medium;

        public string Title { get; set; } = string.Empty;
        public string Message { get; set; } = string.Empty;
        public string? Route { get; set; }
        public string? EntityType { get; set; }
        public Guid? EntityId { get; set; }
        public string? AppVersion { get; set; }
        public string? Platform { get; set; }
        public string? UserAgent { get; set; }
        public DateTime? ResolvedAt { get; set; }
    }
}
