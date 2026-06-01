using HockeyPlanner.Backend.Application.Abstractions.Services;
using HockeyPlanner.Backend.Core.Entities;
using HockeyPlanner.Backend.Core.Enums;
using HockeyPlanner.Backend.Infrastructure.Data;
using HockeyPlanner.Backend.WebAPI.Extensions;
using HockeyPlanner.Backend.WebAPI.Models.Admin;
using HockeyPlanner.Backend.WebAPI.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace HockeyPlanner.Backend.WebAPI.Controllers
{
    [ApiController]
    [Authorize]
    [Route("api/admin")]
    public class AdminController : ControllerBase
    {
        private const string BackendVersion = "0.3.1";
        private readonly AppDbContext _context;
        private readonly IConfiguration _configuration;
        private readonly IWebHostEnvironment _environment;
        private readonly IWebPushService _webPushService;

        public AdminController(
            AppDbContext context,
            IConfiguration configuration,
            IWebHostEnvironment environment,
            IWebPushService webPushService)
        {
            _context = context;
            _configuration = configuration;
            _environment = environment;
            _webPushService = webPushService;
        }

        [HttpGet("dashboard")]
        public async Task<ActionResult<AdminDashboardResponse>> GetDashboard(CancellationToken cancellationToken)
        {
            if (!await this.IsSuperAdminAsync(_context, cancellationToken))
                return Forbid();

            var sevenDaysAgo = DateTime.UtcNow.AddDays(-7);

            var response = new AdminDashboardResponse
            {
                TotalUsers = await _context.Users.CountAsync(cancellationToken),
                TotalTeams = await _context.Teams.CountAsync(cancellationToken),
                TotalEvents = await _context.Events.CountAsync(cancellationToken),
                EventsLast7Days = await _context.Events.CountAsync(value => value.CreatedAt >= sevenDaysAgo, cancellationToken),
                ActivePushSubscriptions = await _context.PushSubscriptions.CountAsync(value => value.IsActive, cancellationToken),
                InactivePushSubscriptions = await _context.PushSubscriptions.CountAsync(value => !value.IsActive, cancellationToken),
                TotalNotifications = await _context.Notifications.CountAsync(cancellationToken),
                FailedDeliveries = await _context.NotificationDeliveries.CountAsync(value => value.Status == NotificationDeliveryStatus.Failed, cancellationToken),
                UnreadReports = await _context.AppReports.CountAsync(value => value.Status == AppReportStatus.New, cancellationToken),
                OpenReports = await _context.AppReports.CountAsync(value => value.Status != AppReportStatus.Resolved && value.Status != AppReportStatus.Rejected, cancellationToken),
                BackendVersion = BackendVersion,
                Environment = _environment.EnvironmentName,
                EmailConfigured = IsEmailConfigured(),
                PushConfigured = _webPushService.IsConfigured,
                ImageKitConfigured = !string.IsNullOrWhiteSpace(_configuration["ImageKit:PrivateKey"]),
            };

            return Ok(response);
        }

        [HttpGet("users")]
        public async Task<ActionResult<AdminUserListResponse>> GetUsers(
            [FromQuery] int page = 1,
            [FromQuery] int pageSize = 25,
            [FromQuery] string? search = null,
            CancellationToken cancellationToken = default)
        {
            if (!await this.IsSuperAdminAsync(_context, cancellationToken))
                return Forbid();

            page = Math.Max(1, page);
            pageSize = Math.Clamp(pageSize, 1, 100);
            var query = _context.Users.AsNoTracking();
            var normalizedSearch = search?.Trim().ToLowerInvariant();

            if (!string.IsNullOrWhiteSpace(normalizedSearch))
            {
                query = query.Where(user =>
                    user.FirstName.ToLower().Contains(normalizedSearch) ||
                    user.LastName.ToLower().Contains(normalizedSearch) ||
                    (user.Email != null && user.Email.ToLower().Contains(normalizedSearch)));
            }

            var total = await query.CountAsync(cancellationToken);
            var users = await query
                .OrderByDescending(user => user.CreatedAt)
                .Skip((page - 1) * pageSize)
                .Take(pageSize)
                .Select(user => new AdminUserDto
                {
                    Id = user.Id,
                    FirstName = user.FirstName,
                    LastName = user.LastName,
                    Email = user.Email,
                    EmailConfirmed = user.EmailConfirmed,
                    AppRole = user.AppRole,
                    CreatedAt = user.CreatedAt,
                    TeamsCount = _context.TeamMemberships.Count(membership => membership.UserId == user.Id),
                    PushSubscriptionsCount = _context.PushSubscriptions.Count(subscription => subscription.UserId == user.Id)
                })
                .ToListAsync(cancellationToken);

            return Ok(new AdminUserListResponse
            {
                Page = page,
                PageSize = pageSize,
                Total = total,
                Items = users
            });
        }

        [HttpGet("reports")]
        public async Task<ActionResult<AdminReportsListResponse>> GetReports(
            [FromQuery] AppReportStatus? status = null,
            [FromQuery] AppReportType? type = null,
            [FromQuery] AppReportSeverity? severity = null,
            [FromQuery] Guid? userId = null,
            [FromQuery] int page = 1,
            [FromQuery] int pageSize = 25,
            CancellationToken cancellationToken = default)
        {
            if (!await this.IsSuperAdminAsync(_context, cancellationToken))
                return Forbid();

            page = Math.Max(1, page);
            pageSize = Math.Clamp(pageSize, 1, 100);
            var query = _context.AppReports
                .AsNoTracking()
                .Include(report => report.User)
                .AsQueryable();

            if (status.HasValue)
                query = query.Where(report => report.Status == status.Value);

            if (type.HasValue)
                query = query.Where(report => report.Type == type.Value);

            if (severity.HasValue)
                query = query.Where(report => report.Severity == severity.Value);

            if (userId.HasValue)
                query = query.Where(report => report.UserId == userId.Value);

            var total = await query.CountAsync(cancellationToken);
            var reportEntities = await query
                .OrderByDescending(report => report.CreatedAt)
                .Skip((page - 1) * pageSize)
                .Take(pageSize)
                .ToListAsync(cancellationToken);
            var reports = reportEntities.Select(ReportsController.MapReport).ToList();

            return Ok(new AdminReportsListResponse
            {
                Page = page,
                PageSize = pageSize,
                Total = total,
                Items = reports
            });
        }

        [HttpGet("reports/{id:guid}")]
        public async Task<ActionResult<AppReportDto>> GetReport(Guid id, CancellationToken cancellationToken)
        {
            if (!await this.IsSuperAdminAsync(_context, cancellationToken))
                return Forbid();

            var report = await _context.AppReports
                .AsNoTracking()
                .Include(value => value.User)
                .FirstOrDefaultAsync(value => value.Id == id, cancellationToken);

            if (report == null)
                return NotFound(new { message = "Обращение не найдено." });

            return Ok(ReportsController.MapReport(report));
        }

        [HttpPut("reports/{id:guid}/status")]
        public async Task<ActionResult<AppReportDto>> UpdateReportStatus(Guid id, [FromBody] UpdateReportStatusRequest request, CancellationToken cancellationToken)
        {
            if (!await this.IsSuperAdminAsync(_context, cancellationToken))
                return Forbid();

            if (!Enum.IsDefined(request.Status))
                return BadRequest(new { message = "Некорректный статус обращения." });

            var report = await _context.AppReports
                .Include(value => value.User)
                .FirstOrDefaultAsync(value => value.Id == id, cancellationToken);

            if (report == null)
                return NotFound(new { message = "Обращение не найдено." });

            report.Status = request.Status;
            report.UpdatedAt = DateTime.UtcNow;
            report.ResolvedAt = request.Status is AppReportStatus.Resolved or AppReportStatus.Rejected
                ? DateTime.UtcNow
                : null;

            await _context.SaveChangesAsync(cancellationToken);
            return Ok(ReportsController.MapReport(report));
        }

        [HttpGet("releases")]
        public async Task<ActionResult<IReadOnlyCollection<ReleaseNoticeDto>>> GetReleases(CancellationToken cancellationToken)
        {
            if (!await this.IsSuperAdminAsync(_context, cancellationToken))
                return Forbid();

            var releases = await _context.ReleaseNotices
                .AsNoTracking()
                .OrderByDescending(value => value.CreatedAt)
                .ToListAsync(cancellationToken);

            return Ok(releases.Select(MapReleaseNotice).ToList());
        }

        [HttpGet("releases/{id:guid}")]
        public async Task<ActionResult<ReleaseNoticeDto>> GetRelease(Guid id, CancellationToken cancellationToken)
        {
            if (!await this.IsSuperAdminAsync(_context, cancellationToken))
                return Forbid();

            var release = await _context.ReleaseNotices
                .AsNoTracking()
                .FirstOrDefaultAsync(value => value.Id == id, cancellationToken);

            return release == null ? NotFound() : Ok(MapReleaseNotice(release));
        }

        [HttpPost("releases")]
        public async Task<ActionResult<ReleaseNoticeDto>> CreateRelease([FromBody] CreateUpdateReleaseNoticeRequest request, CancellationToken cancellationToken)
        {
            if (!await this.IsSuperAdminAsync(_context, cancellationToken))
                return Forbid();

            var validation = ValidateReleaseRequest(request);
            if (validation != null)
                return BadRequest(new { message = validation });

            var now = DateTime.UtcNow;
            var release = new ReleaseNotice
            {
                Version = request.Version.Trim(),
                Title = request.Title.Trim(),
                Body = request.Body.Trim(),
                SendNotification = request.SendNotification,
                CreatedByUserId = User.GetUserId(),
                CreatedAt = now,
                UpdatedAt = now
            };

            await _context.ReleaseNotices.AddAsync(release, cancellationToken);
            await _context.SaveChangesAsync(cancellationToken);

            return CreatedAtAction(nameof(GetRelease), new { id = release.Id }, MapReleaseNotice(release));
        }

        [HttpPut("releases/{id:guid}")]
        public async Task<ActionResult<ReleaseNoticeDto>> UpdateRelease(Guid id, [FromBody] CreateUpdateReleaseNoticeRequest request, CancellationToken cancellationToken)
        {
            if (!await this.IsSuperAdminAsync(_context, cancellationToken))
                return Forbid();

            var validation = ValidateReleaseRequest(request);
            if (validation != null)
                return BadRequest(new { message = validation });

            var release = await _context.ReleaseNotices.FirstOrDefaultAsync(value => value.Id == id, cancellationToken);
            if (release == null)
                return NotFound();

            release.Version = request.Version.Trim();
            release.Title = request.Title.Trim();
            release.Body = request.Body.Trim();
            release.SendNotification = request.SendNotification;
            release.UpdatedAt = DateTime.UtcNow;

            await _context.SaveChangesAsync(cancellationToken);
            return Ok(MapReleaseNotice(release));
        }

        [HttpPost("releases/{id:guid}/publish")]
        public async Task<ActionResult<ReleaseNoticeDto>> PublishRelease(
            Guid id,
            [FromServices] INotificationService notificationService,
            CancellationToken cancellationToken)
        {
            if (!await this.IsSuperAdminAsync(_context, cancellationToken))
                return Forbid();

            var release = await _context.ReleaseNotices.FirstOrDefaultAsync(value => value.Id == id, cancellationToken);
            if (release == null)
                return NotFound();

            var now = DateTime.UtcNow;
            if (!release.IsPublished)
            {
                release.IsPublished = true;
                release.PublishedAt = now;
                release.UpdatedAt = now;
            }

            var shouldSendNotification = release.SendNotification && !release.NotificationSent;
            await _context.SaveChangesAsync(cancellationToken);

            if (shouldSendNotification)
            {
                var userIds = await _context.Users
                    .AsNoTracking()
                    .Select(user => user.Id)
                    .ToListAsync(cancellationToken);

                await notificationService.NotifyUsersAsync(
                    userIds,
                    NotificationType.AppUpdatePublished,
                    NotificationCategory.AppUpdates,
                    release.Title,
                    ToPreview(release.Body),
                    "/settings",
                    cancellationToken);

                release.NotificationSent = true;
                release.UpdatedAt = DateTime.UtcNow;
                await _context.SaveChangesAsync(cancellationToken);
            }

            return Ok(MapReleaseNotice(release));
        }

        [HttpGet("notification-deliveries/summary")]
        public async Task<ActionResult<NotificationDeliverySummaryResponse>> GetNotificationDeliverySummary(
            [FromQuery] NotificationDeliveryStatus? status = null,
            [FromQuery] Guid? userId = null,
            [FromQuery] Guid? notificationId = null,
            [FromQuery] DateTime? dateFrom = null,
            [FromQuery] DateTime? dateTo = null,
            CancellationToken cancellationToken = default)
        {
            if (!await this.IsSuperAdminAsync(_context, cancellationToken))
                return Forbid();

            var query = ApplyDeliveryFilters(_context.NotificationDeliveries.AsNoTracking(), status, userId, notificationId, dateFrom, dateTo);

            var response = new NotificationDeliverySummaryResponse
            {
                Total = await query.CountAsync(cancellationToken),
                Sent = await query.CountAsync(value => value.Status == NotificationDeliveryStatus.Sent, cancellationToken),
                Failed = await query.CountAsync(value => value.Status == NotificationDeliveryStatus.Failed, cancellationToken),
                Skipped = await query.CountAsync(value => value.Status == NotificationDeliveryStatus.Skipped, cancellationToken),
                EndpointInactive = await query.CountAsync(value => value.Status == NotificationDeliveryStatus.EndpointInactive, cancellationToken),
                ActivePushSubscriptions = await _context.PushSubscriptions.CountAsync(value => value.IsActive, cancellationToken),
                InactivePushSubscriptions = await _context.PushSubscriptions.CountAsync(value => !value.IsActive, cancellationToken)
            };

            return Ok(response);
        }

        [HttpGet("notification-deliveries")]
        public async Task<ActionResult<NotificationDeliveryListResponse>> GetNotificationDeliveries(
            [FromQuery] NotificationDeliveryStatus? status = null,
            [FromQuery] Guid? userId = null,
            [FromQuery] Guid? notificationId = null,
            [FromQuery] DateTime? dateFrom = null,
            [FromQuery] DateTime? dateTo = null,
            [FromQuery] int page = 1,
            [FromQuery] int pageSize = 25,
            CancellationToken cancellationToken = default)
        {
            if (!await this.IsSuperAdminAsync(_context, cancellationToken))
                return Forbid();

            page = Math.Max(1, page);
            pageSize = Math.Clamp(pageSize, 1, 100);

            var query = ApplyDeliveryFilters(
                _context.NotificationDeliveries
                    .AsNoTracking()
                    .Include(value => value.User)
                    .Include(value => value.Notification),
                status,
                userId,
                notificationId,
                dateFrom,
                dateTo);

            var total = await query.CountAsync(cancellationToken);
            var deliveries = await query
                .OrderByDescending(value => value.CreatedAt)
                .Skip((page - 1) * pageSize)
                .Take(pageSize)
                .ToListAsync(cancellationToken);

            return Ok(new NotificationDeliveryListResponse
            {
                Page = page,
                PageSize = pageSize,
                Total = total,
                Items = deliveries.Select(MapNotificationDelivery).ToList()
            });
        }

        [HttpPost("notifications/test")]
        public async Task<IActionResult> SendTestNotification(
            [FromServices] INotificationService notificationService,
            CancellationToken cancellationToken)
        {
            if (!await this.IsSuperAdminAsync(_context, cancellationToken))
                return Forbid();

            var userId = User.GetUserId();
            if (!userId.HasValue)
                return Unauthorized();

            await notificationService.NotifyUserAsync(
                userId.Value,
                NotificationType.AppUpdatePublished,
                NotificationCategory.AppUpdates,
                "Тестовое уведомление",
                "Проверка доставки уведомлений из админ-панели.",
                "/admin");

            return Ok(new { success = true });
        }

        private bool IsEmailConfigured()
        {
            var provider = _configuration["Email:Provider"] ?? _configuration["EMAIL_PROVIDER"];
            if (provider?.Equals("Resend", StringComparison.OrdinalIgnoreCase) == true)
            {
                return !string.IsNullOrWhiteSpace(_configuration["Resend:ApiKey"]) ||
                       !string.IsNullOrWhiteSpace(_configuration["RESEND_API_KEY"]);
            }

            return (!string.IsNullOrWhiteSpace(_configuration["Email:SmtpHost"]) ||
                    !string.IsNullOrWhiteSpace(_configuration["SMTP_HOST"])) &&
                   (!string.IsNullOrWhiteSpace(_configuration["Email:SmtpUser"]) ||
                    !string.IsNullOrWhiteSpace(_configuration["SMTP_USER"])) &&
                   (!string.IsNullOrWhiteSpace(_configuration["Email:SmtpPassword"]) ||
                    !string.IsNullOrWhiteSpace(_configuration["SMTP_PASSWORD"]));
        }

        private static IQueryable<NotificationDelivery> ApplyDeliveryFilters(
            IQueryable<NotificationDelivery> query,
            NotificationDeliveryStatus? status,
            Guid? userId,
            Guid? notificationId,
            DateTime? dateFrom,
            DateTime? dateTo)
        {
            if (status.HasValue)
                query = query.Where(value => value.Status == status.Value);

            if (userId.HasValue)
                query = query.Where(value => value.UserId == userId.Value);

            if (notificationId.HasValue)
                query = query.Where(value => value.NotificationId == notificationId.Value);

            if (dateFrom.HasValue)
                query = query.Where(value => value.CreatedAt >= dateFrom.Value);

            if (dateTo.HasValue)
                query = query.Where(value => value.CreatedAt <= dateTo.Value);

            return query;
        }

        private static ReleaseNoticeDto MapReleaseNotice(ReleaseNotice release)
        {
            return new ReleaseNoticeDto
            {
                Id = release.Id,
                Version = release.Version,
                Title = release.Title,
                Body = release.Body,
                IsPublished = release.IsPublished,
                SendNotification = release.SendNotification,
                NotificationSent = release.NotificationSent,
                PublishedAt = release.PublishedAt,
                CreatedAt = release.CreatedAt,
                UpdatedAt = release.UpdatedAt,
                CreatedByUserId = release.CreatedByUserId
            };
        }

        private static NotificationDeliveryDto MapNotificationDelivery(NotificationDelivery delivery)
        {
            return new NotificationDeliveryDto
            {
                Id = delivery.Id,
                NotificationId = delivery.NotificationId,
                UserId = delivery.UserId,
                UserName = delivery.User == null ? null : $"{delivery.User.FirstName} {delivery.User.LastName}".Trim(),
                UserEmail = delivery.User?.Email,
                PushSubscriptionId = delivery.PushSubscriptionId,
                Status = delivery.Status,
                Error = delivery.Error,
                EndpointHash = delivery.EndpointHash,
                CreatedAt = delivery.CreatedAt,
                SentAt = delivery.SentAt,
                NotificationTitle = delivery.Notification?.Title,
                NotificationType = delivery.Notification?.Type,
                NotificationCategory = delivery.Notification?.Category
            };
        }

        private static string? ValidateReleaseRequest(CreateUpdateReleaseNoticeRequest request)
        {
            if (string.IsNullOrWhiteSpace(request.Version))
                return "Version is required.";

            if (string.IsNullOrWhiteSpace(request.Title))
                return "Title is required.";

            if (string.IsNullOrWhiteSpace(request.Body))
                return "Body is required.";

            if (request.Version.Trim().Length > 50)
                return "Version is too long.";

            if (request.Title.Trim().Length > 180)
                return "Title is too long.";

            if (request.Body.Trim().Length > 4000)
                return "Body is too long.";

            return null;
        }

        private static string ToPreview(string body)
        {
            var trimmed = body.Trim();
            return trimmed.Length <= 220 ? trimmed : trimmed[..220];
        }
    }
}
