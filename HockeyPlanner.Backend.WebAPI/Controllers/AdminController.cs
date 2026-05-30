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
        private const string BackendVersion = "0.2.0";
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
                FailedDeliveries = 0,
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

        [HttpPost("notifications/test")]
        public async Task<IActionResult> SendTestNotification(
            [FromServices] HockeyPlanner.Backend.Application.Abstractions.Services.INotificationService notificationService,
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
            return !string.IsNullOrWhiteSpace(_configuration["Email:SmtpHost"]) &&
                   !string.IsNullOrWhiteSpace(_configuration["Email:SmtpUser"]) &&
                   !string.IsNullOrWhiteSpace(_configuration["Email:SmtpPassword"]);
        }
    }
}
