using HockeyPlanner.Backend.Core.Enums;

namespace HockeyPlanner.Backend.WebAPI.Models.Admin
{
    public sealed class AdminDashboardResponse
    {
        public int TotalUsers { get; set; }
        public int TotalTeams { get; set; }
        public int TotalEvents { get; set; }
        public int EventsLast7Days { get; set; }
        public int ActivePushSubscriptions { get; set; }
        public int InactivePushSubscriptions { get; set; }
        public int TotalNotifications { get; set; }
        public int FailedDeliveries { get; set; }
        public int UnreadReports { get; set; }
        public int OpenReports { get; set; }
        public string BackendVersion { get; set; } = string.Empty;
        public string Environment { get; set; } = string.Empty;
        public bool EmailConfigured { get; set; }
        public bool PushConfigured { get; set; }
        public bool ImageKitConfigured { get; set; }
    }

    public sealed class AdminUserListResponse
    {
        public int Page { get; set; }
        public int PageSize { get; set; }
        public int Total { get; set; }
        public List<AdminUserDto> Items { get; set; } = new();
    }

    public sealed class AdminUserDto
    {
        public Guid Id { get; set; }
        public string FirstName { get; set; } = string.Empty;
        public string LastName { get; set; } = string.Empty;
        public string? Email { get; set; }
        public bool EmailConfirmed { get; set; }
        public AppRole AppRole { get; set; }
        public DateTime CreatedAt { get; set; }
        public int TeamsCount { get; set; }
        public int PushSubscriptionsCount { get; set; }
    }

    public sealed class AdminReportsListResponse
    {
        public int Page { get; set; }
        public int PageSize { get; set; }
        public int Total { get; set; }
        public List<AppReportDto> Items { get; set; } = new();
    }

    public sealed class AppReportDto
    {
        public Guid Id { get; set; }
        public Guid? UserId { get; set; }
        public string? UserName { get; set; }
        public string? UserEmail { get; set; }
        public AppReportType Type { get; set; }
        public AppReportStatus Status { get; set; }
        public AppReportSeverity Severity { get; set; }
        public string Title { get; set; } = string.Empty;
        public string Message { get; set; } = string.Empty;
        public string? Route { get; set; }
        public string? EntityType { get; set; }
        public Guid? EntityId { get; set; }
        public string? AppVersion { get; set; }
        public string? Platform { get; set; }
        public string? UserAgent { get; set; }
        public DateTime CreatedAt { get; set; }
        public DateTime? UpdatedAt { get; set; }
        public DateTime? ResolvedAt { get; set; }
    }

    public sealed class UpdateReportStatusRequest
    {
        public AppReportStatus Status { get; set; }
    }

    public sealed class ReleaseNoticeDto
    {
        public Guid Id { get; set; }
        public string Version { get; set; } = string.Empty;
        public string Title { get; set; } = string.Empty;
        public string Body { get; set; } = string.Empty;
        public bool IsPublished { get; set; }
        public bool SendNotification { get; set; }
        public bool NotificationSent { get; set; }
        public DateTime? PublishedAt { get; set; }
        public DateTime CreatedAt { get; set; }
        public DateTime? UpdatedAt { get; set; }
        public Guid? CreatedByUserId { get; set; }
    }

    public sealed class CreateUpdateReleaseNoticeRequest
    {
        public string Version { get; set; } = string.Empty;
        public string Title { get; set; } = string.Empty;
        public string Body { get; set; } = string.Empty;
        public bool SendNotification { get; set; }
    }

    public sealed class NotificationDeliverySummaryResponse
    {
        public int Total { get; set; }
        public int Sent { get; set; }
        public int Failed { get; set; }
        public int Skipped { get; set; }
        public int EndpointInactive { get; set; }
        public int ActivePushSubscriptions { get; set; }
        public int InactivePushSubscriptions { get; set; }
    }

    public sealed class NotificationDeliveryListResponse
    {
        public int Page { get; set; }
        public int PageSize { get; set; }
        public int Total { get; set; }
        public List<NotificationDeliveryDto> Items { get; set; } = new();
    }

    public sealed class NotificationDeliveryDto
    {
        public Guid Id { get; set; }
        public Guid NotificationId { get; set; }
        public Guid UserId { get; set; }
        public string? UserName { get; set; }
        public string? UserEmail { get; set; }
        public Guid? PushSubscriptionId { get; set; }
        public NotificationDeliveryStatus Status { get; set; }
        public string? Error { get; set; }
        public string? EndpointHash { get; set; }
        public DateTime CreatedAt { get; set; }
        public DateTime? SentAt { get; set; }
        public string? NotificationTitle { get; set; }
        public NotificationType? NotificationType { get; set; }
        public NotificationCategory? NotificationCategory { get; set; }
    }

    public sealed class CreateAppReportRequest
    {
        public AppReportType Type { get; set; } = AppReportType.Other;
        public AppReportSeverity? Severity { get; set; }
        public string Title { get; set; } = string.Empty;
        public string Message { get; set; } = string.Empty;
        public string? Route { get; set; }
        public string? EntityType { get; set; }
        public Guid? EntityId { get; set; }
        public string? AppVersion { get; set; }
        public string? Platform { get; set; }
        public string? UserAgent { get; set; }
    }
}
