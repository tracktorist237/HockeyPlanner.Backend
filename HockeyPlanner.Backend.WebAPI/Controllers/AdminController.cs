using HockeyPlanner.Backend.Application.Abstractions.Services;
using HockeyPlanner.Backend.Core.Entities;
using HockeyPlanner.Backend.Core.Enums;
using HockeyPlanner.Backend.Core.Exceptions;
using HockeyPlanner.Backend.Infrastructure.Data;
using HockeyPlanner.Backend.WebAPI.Extensions;
using HockeyPlanner.Backend.WebAPI.Models.Admin;
using HockeyPlanner.Backend.WebAPI.Models.Instructions;
using HockeyPlanner.Backend.WebAPI.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.ComponentModel;
using System.Data.Common;
using System.Diagnostics;
using System.Text.RegularExpressions;

namespace HockeyPlanner.Backend.WebAPI.Controllers
{
    [ApiController]
    [Authorize]
    [Route("api/admin")]
    public class AdminController : ControllerBase
    {
        private const string BackendVersion = "0.3.3";
        private readonly AppDbContext _context;
        private readonly IConfiguration _configuration;
        private readonly IWebHostEnvironment _environment;
        private readonly IWebPushService _webPushService;
        private readonly IImageKitUploader _imageKitUploader;
        private readonly ILogger<AdminController> _logger;

        public AdminController(
            AppDbContext context,
            IConfiguration configuration,
            IWebHostEnvironment environment,
            IWebPushService webPushService,
            IImageKitUploader imageKitUploader,
            ILogger<AdminController> logger)
        {
            _context = context;
            _configuration = configuration;
            _environment = environment;
            _webPushService = webPushService;
            _imageKitUploader = imageKitUploader;
            _logger = logger;
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
            [FromQuery] AppRole? appRole = null,
            [FromQuery] UserRole? role = null,
            [FromQuery] bool? emailConfirmed = null,
            [FromQuery] bool? hasTeams = null,
            [FromQuery] bool? hasPush = null,
            [FromQuery] string? sortBy = null,
            [FromQuery] string? sortDirection = null,
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

            if (appRole.HasValue)
                query = query.Where(user => user.AppRole == appRole.Value);

            if (role.HasValue)
                query = query.Where(user => user.Role == role.Value);

            if (emailConfirmed.HasValue)
                query = query.Where(user => user.EmailConfirmed == emailConfirmed.Value);

            if (hasTeams.HasValue)
            {
                query = hasTeams.Value
                    ? query.Where(user => _context.TeamMemberships.Any(membership => membership.UserId == user.Id))
                    : query.Where(user => !_context.TeamMemberships.Any(membership => membership.UserId == user.Id));
            }

            if (hasPush.HasValue)
            {
                query = hasPush.Value
                    ? query.Where(user => _context.PushSubscriptions.Any(subscription => subscription.UserId == user.Id && subscription.IsActive))
                    : query.Where(user => !_context.PushSubscriptions.Any(subscription => subscription.UserId == user.Id && subscription.IsActive));
            }

            var total = await query.CountAsync(cancellationToken);
            var sortKey = sortBy?.Trim().ToLowerInvariant();
            var descending = !string.Equals(sortDirection, "asc", StringComparison.OrdinalIgnoreCase);
            query = sortKey switch
            {
                "name" => descending
                    ? query.OrderByDescending(user => user.LastName).ThenByDescending(user => user.FirstName)
                    : query.OrderBy(user => user.LastName).ThenBy(user => user.FirstName),
                "email" => descending
                    ? query.OrderByDescending(user => user.Email)
                    : query.OrderBy(user => user.Email),
                "role" => descending
                    ? query.OrderByDescending(user => user.Role).ThenBy(user => user.LastName)
                    : query.OrderBy(user => user.Role).ThenBy(user => user.LastName),
                "appRole" => descending
                    ? query.OrderByDescending(user => user.AppRole).ThenBy(user => user.LastName)
                    : query.OrderBy(user => user.AppRole).ThenBy(user => user.LastName),
                "teams" => descending
                    ? query.OrderByDescending(user => _context.TeamMemberships.Count(membership => membership.UserId == user.Id)).ThenBy(user => user.LastName)
                    : query.OrderBy(user => _context.TeamMemberships.Count(membership => membership.UserId == user.Id)).ThenBy(user => user.LastName),
                "push" => descending
                    ? query.OrderByDescending(user => _context.PushSubscriptions.Count(subscription => subscription.UserId == user.Id)).ThenBy(user => user.LastName)
                    : query.OrderBy(user => _context.PushSubscriptions.Count(subscription => subscription.UserId == user.Id)).ThenBy(user => user.LastName),
                _ => descending
                    ? query.OrderByDescending(user => user.CreatedAt)
                    : query.OrderBy(user => user.CreatedAt),
            };

            var users = await query
                .Skip((page - 1) * pageSize)
                .Take(pageSize)
                .Select(user => new AdminUserDto
                {
                    Id = user.Id,
                    FirstName = user.FirstName,
                    LastName = user.LastName,
                    Email = user.Email,
                    EmailConfirmed = user.EmailConfirmed,
                    Role = user.Role,
                    AppRole = user.AppRole,
                    Phone = user.Phone,
                    JerseyNumber = user.JerseyNumber,
                    CreatedAt = user.CreatedAt,
                    TeamsCount = _context.TeamMemberships.Count(membership => membership.UserId == user.Id),
                    PushSubscriptionsCount = _context.PushSubscriptions.Count(subscription => subscription.UserId == user.Id),
                    Teams = _context.TeamMemberships
                        .Where(membership => membership.UserId == user.Id)
                        .OrderBy(membership => membership.Team.Name)
                        .Select(membership => new AdminUserTeamDto
                        {
                            TeamId = membership.TeamId,
                            TeamName = membership.Team.Name,
                            Role = membership.Role,
                            BadgeTitle = membership.BadgeTitle
                        })
                        .ToList()
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

        [HttpPut("users/{id:guid}")]
        public async Task<ActionResult<AdminUserDto>> UpdateUser(Guid id, [FromBody] UpdateAdminUserRequest request, CancellationToken cancellationToken)
        {
            if (!await this.IsSuperAdminAsync(_context, cancellationToken))
                return Forbid();

            if (string.IsNullOrWhiteSpace(request.FirstName) || string.IsNullOrWhiteSpace(request.LastName))
                return BadRequest(new { message = "Имя и фамилия обязательны." });

            if (!Enum.IsDefined(typeof(UserRole), request.Role))
                return BadRequest(new { message = "Некорректная роль пользователя." });

            if (!Enum.IsDefined(typeof(AppRole), request.AppRole))
                return BadRequest(new { message = "Некорректная роль приложения." });

            var user = await _context.Users.FirstOrDefaultAsync(value => value.Id == id, cancellationToken);
            if (user == null)
                return NotFound(new { message = "Пользователь не найден." });

            var currentUserId = User.GetUserId();
            if (currentUserId == id && request.AppRole != AppRole.SuperAdmin)
                return BadRequest(new { message = "Нельзя снять SuperAdmin у текущего пользователя." });

            if (user.AppRole == AppRole.SuperAdmin && request.AppRole != AppRole.SuperAdmin)
            {
                var superAdminsCount = await _context.Users.CountAsync(value => value.AppRole == AppRole.SuperAdmin, cancellationToken);
                if (superAdminsCount <= 1)
                    return BadRequest(new { message = "Нельзя снять последнего SuperAdmin." });
            }

            var normalizedEmail = string.IsNullOrWhiteSpace(request.Email) ? null : request.Email.Trim();
            if (normalizedEmail != null)
            {
                var normalizedEmailLower = normalizedEmail.ToLowerInvariant();
                var emailExists = await _context.Users
                    .AsNoTracking()
                    .AnyAsync(value => value.Id != id && value.Email != null && value.Email.ToLower() == normalizedEmailLower, cancellationToken);

                if (emailExists)
                    return Conflict(new { message = "Пользователь с таким email уже существует." });
            }

            user.FirstName = request.FirstName.Trim();
            user.LastName = request.LastName.Trim();
            user.Email = normalizedEmail;
            user.EmailConfirmed = request.EmailConfirmed;
            user.Role = request.Role;
            user.AppRole = request.AppRole;
            user.Phone = string.IsNullOrWhiteSpace(request.Phone) ? null : request.Phone.Trim();
            user.JerseyNumber = request.JerseyNumber;
            user.UpdatedAt = DateTime.UtcNow;

            await _context.SaveChangesAsync(cancellationToken);

            return Ok(new AdminUserDto
            {
                Id = user.Id,
                FirstName = user.FirstName,
                LastName = user.LastName,
                Email = user.Email,
                EmailConfirmed = user.EmailConfirmed,
                Role = user.Role,
                AppRole = user.AppRole,
                Phone = user.Phone,
                JerseyNumber = user.JerseyNumber,
                CreatedAt = user.CreatedAt,
                TeamsCount = await _context.TeamMemberships.CountAsync(membership => membership.UserId == user.Id, cancellationToken),
                PushSubscriptionsCount = await _context.PushSubscriptions.CountAsync(subscription => subscription.UserId == user.Id, cancellationToken),
                Teams = await _context.TeamMemberships
                    .AsNoTracking()
                    .Where(membership => membership.UserId == user.Id)
                    .OrderBy(membership => membership.Team.Name)
                    .Select(membership => new AdminUserTeamDto
                    {
                        TeamId = membership.TeamId,
                        TeamName = membership.Team.Name,
                        Role = membership.Role,
                        BadgeTitle = membership.BadgeTitle
                    })
                    .ToListAsync(cancellationToken)
            });
        }

        [HttpGet("teams")]
        public async Task<ActionResult<AdminTeamListResponse>> GetTeams(
            [FromQuery] int page = 1,
            [FromQuery] int pageSize = 100,
            [FromQuery] string? search = null,
            CancellationToken cancellationToken = default)
        {
            if (!await this.IsSuperAdminAsync(_context, cancellationToken))
                return Forbid();

            page = Math.Max(1, page);
            pageSize = Math.Clamp(pageSize, 1, 200);
            var query = _context.Teams.AsNoTracking();
            var normalizedSearch = search?.Trim().ToLowerInvariant();

            if (!string.IsNullOrWhiteSpace(normalizedSearch))
            {
                query = query.Where(team => team.Name.ToLower().Contains(normalizedSearch));
            }

            var total = await query.CountAsync(cancellationToken);
            var teams = await query
                .OrderBy(team => team.Name)
                .Skip((page - 1) * pageSize)
                .Take(pageSize)
                .Select(team => new AdminTeamDto
                {
                    Id = team.Id,
                    Name = team.Name,
                    Visibility = team.Visibility,
                    MembersCount = team.Memberships.Count
                })
                .ToListAsync(cancellationToken);

            return Ok(new AdminTeamListResponse
            {
                Page = page,
                PageSize = pageSize,
                Total = total,
                Items = teams
            });
        }

        [HttpPost("teams/{teamId:guid}/members")]
        public async Task<ActionResult<AdminUserDto>> AddTeamMember(Guid teamId, [FromBody] AddAdminTeamMemberRequest request, CancellationToken cancellationToken)
        {
            if (!await this.IsSuperAdminAsync(_context, cancellationToken))
                return Forbid();

            if (request.UserId == Guid.Empty)
                return BadRequest(new { message = "Выберите пользователя." });

            if (!Enum.IsDefined(typeof(TeamMemberRole), request.Role))
                return BadRequest(new { message = "Некорректная роль в команде." });

            var teamExists = await _context.Teams.AsNoTracking().AnyAsync(team => team.Id == teamId, cancellationToken);
            if (!teamExists)
                return NotFound(new { message = "Команда не найдена." });

            var user = await _context.Users.FirstOrDefaultAsync(value => value.Id == request.UserId, cancellationToken);
            if (user == null)
                return NotFound(new { message = "Пользователь не найден." });

            var membershipExists = await _context.TeamMemberships
                .AsNoTracking()
                .AnyAsync(value => value.TeamId == teamId && value.UserId == request.UserId, cancellationToken);

            if (membershipExists)
                return Conflict(new { message = "Пользователь уже состоит в этой команде." });

            var membership = new TeamMembership
            {
                TeamId = teamId,
                UserId = request.UserId,
                Role = request.Role,
                BadgeTitle = string.IsNullOrWhiteSpace(request.BadgeTitle) ? null : request.BadgeTitle.Trim(),
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            };

            await _context.TeamMemberships.AddAsync(membership, cancellationToken);
            await _context.SaveChangesAsync(cancellationToken);

            return Ok(new AdminUserDto
            {
                Id = user.Id,
                FirstName = user.FirstName,
                LastName = user.LastName,
                Email = user.Email,
                EmailConfirmed = user.EmailConfirmed,
                Role = user.Role,
                AppRole = user.AppRole,
                Phone = user.Phone,
                JerseyNumber = user.JerseyNumber,
                CreatedAt = user.CreatedAt,
                TeamsCount = await _context.TeamMemberships.CountAsync(value => value.UserId == user.Id, cancellationToken),
                PushSubscriptionsCount = await _context.PushSubscriptions.CountAsync(value => value.UserId == user.Id, cancellationToken),
                Teams = await _context.TeamMemberships
                    .AsNoTracking()
                    .Where(value => value.UserId == user.Id)
                    .OrderBy(value => value.Team.Name)
                    .Select(value => new AdminUserTeamDto
                    {
                        TeamId = value.TeamId,
                        TeamName = value.Team.Name,
                        Role = value.Role,
                        BadgeTitle = value.BadgeTitle
                    })
                    .ToListAsync(cancellationToken)
            });
        }

        [HttpDelete("teams/{teamId:guid}/members/{userId:guid}")]
        public async Task<IActionResult> RemoveTeamMember(Guid teamId, Guid userId, CancellationToken cancellationToken)
        {
            if (!await this.IsSuperAdminAsync(_context, cancellationToken))
                return Forbid();

            var membership = await _context.TeamMemberships
                .FirstOrDefaultAsync(value => value.TeamId == teamId && value.UserId == userId, cancellationToken);

            if (membership == null)
                return NotFound(new { message = "Участник команды не найден." });

            _context.TeamMemberships.Remove(membership);
            await _context.SaveChangesAsync(cancellationToken);

            return NoContent();
        }

        [HttpPut("teams/{teamId:guid}/members/{userId:guid}")]
        public async Task<ActionResult<AdminUserDto>> UpdateTeamMember(Guid teamId, Guid userId, [FromBody] UpdateAdminTeamMemberRequest request, CancellationToken cancellationToken)
        {
            if (!await this.IsSuperAdminAsync(_context, cancellationToken))
                return Forbid();

            if (!Enum.IsDefined(typeof(TeamMemberRole), request.Role))
                return BadRequest(new { message = "Некорректная роль в команде." });

            var membership = await _context.TeamMemberships
                .Include(value => value.User)
                .FirstOrDefaultAsync(value => value.TeamId == teamId && value.UserId == userId, cancellationToken);

            if (membership == null)
                return NotFound(new { message = "Участник команды не найден." });

            membership.Role = request.Role;
            membership.BadgeTitle = string.IsNullOrWhiteSpace(request.BadgeTitle) ? null : request.BadgeTitle.Trim();
            membership.UpdatedAt = DateTime.UtcNow;

            await _context.SaveChangesAsync(cancellationToken);

            var user = membership.User;
            return Ok(new AdminUserDto
            {
                Id = user.Id,
                FirstName = user.FirstName,
                LastName = user.LastName,
                Email = user.Email,
                EmailConfirmed = user.EmailConfirmed,
                Role = user.Role,
                AppRole = user.AppRole,
                Phone = user.Phone,
                JerseyNumber = user.JerseyNumber,
                CreatedAt = user.CreatedAt,
                TeamsCount = await _context.TeamMemberships.CountAsync(value => value.UserId == user.Id, cancellationToken),
                PushSubscriptionsCount = await _context.PushSubscriptions.CountAsync(value => value.UserId == user.Id, cancellationToken),
                Teams = await _context.TeamMemberships
                    .AsNoTracking()
                    .Where(value => value.UserId == user.Id)
                    .OrderBy(value => value.Team.Name)
                    .Select(value => new AdminUserTeamDto
                    {
                        TeamId = value.TeamId,
                        TeamName = value.Team.Name,
                        Role = value.Role,
                        BadgeTitle = value.BadgeTitle
                    })
                    .ToListAsync(cancellationToken)
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

        [HttpGet("instructions")]
        public async Task<ActionResult<IReadOnlyCollection<InstructionArticleDto>>> GetInstructions(CancellationToken cancellationToken)
        {
            if (!await this.IsSuperAdminAsync(_context, cancellationToken))
                return Forbid();

            var articles = await _context.InstructionArticles
                .AsNoTracking()
                .OrderBy(article => article.SortOrder)
                .ThenBy(article => article.Title)
                .ToListAsync(cancellationToken);

            return Ok(articles.Select(InstructionsController.MapArticle).ToList());
        }

        [HttpGet("instructions/{id:guid}")]
        public async Task<ActionResult<InstructionArticleDto>> GetInstruction(Guid id, CancellationToken cancellationToken)
        {
            if (!await this.IsSuperAdminAsync(_context, cancellationToken))
                return Forbid();

            var article = await _context.InstructionArticles
                .AsNoTracking()
                .FirstOrDefaultAsync(value => value.Id == id, cancellationToken);

            return article == null ? NotFound() : Ok(InstructionsController.MapArticle(article));
        }

        [HttpPost("instructions")]
        public async Task<ActionResult<InstructionArticleDto>> CreateInstruction(
            [FromBody] CreateUpdateInstructionArticleRequest request,
            CancellationToken cancellationToken)
        {
            if (!await this.IsSuperAdminAsync(_context, cancellationToken))
                return Forbid();

            var validation = ValidateInstructionRequest(request, out var slug);
            if (validation != null)
                return BadRequest(new { message = validation });

            if (await _context.InstructionArticles.AnyAsync(article => article.Slug == slug, cancellationToken))
                return Conflict(new { message = "Instruction slug already exists." });

            var now = DateTime.UtcNow;
            var article = new InstructionArticle
            {
                Slug = slug,
                Title = request.Title.Trim(),
                Summary = NormalizeOptional(request.Summary),
                Content = request.Content.Trim(),
                ImageUrl = NormalizeOptional(request.ImageUrl),
                IsPublished = request.IsPublished,
                SortOrder = request.SortOrder,
                PublishedAt = request.IsPublished ? now : null,
                CreatedByUserId = User.GetUserId()
            };

            await _context.InstructionArticles.AddAsync(article, cancellationToken);
            await _context.SaveChangesAsync(cancellationToken);

            return CreatedAtAction(nameof(GetInstruction), new { id = article.Id }, InstructionsController.MapArticle(article));
        }

        [HttpPut("instructions/{id:guid}")]
        public async Task<ActionResult<InstructionArticleDto>> UpdateInstruction(
            Guid id,
            [FromBody] CreateUpdateInstructionArticleRequest request,
            CancellationToken cancellationToken)
        {
            if (!await this.IsSuperAdminAsync(_context, cancellationToken))
                return Forbid();

            var validation = ValidateInstructionRequest(request, out var slug);
            if (validation != null)
                return BadRequest(new { message = validation });

            var article = await _context.InstructionArticles.FirstOrDefaultAsync(value => value.Id == id, cancellationToken);
            if (article == null)
                return NotFound();

            if (await _context.InstructionArticles.AnyAsync(value => value.Id != id && value.Slug == slug, cancellationToken))
                return Conflict(new { message = "Instruction slug already exists." });

            var wasPublished = article.IsPublished;
            article.Slug = slug;
            article.Title = request.Title.Trim();
            article.Summary = NormalizeOptional(request.Summary);
            article.Content = request.Content.Trim();
            article.ImageUrl = NormalizeOptional(request.ImageUrl);
            article.IsPublished = request.IsPublished;
            article.SortOrder = request.SortOrder;
            article.PublishedAt = request.IsPublished
                ? article.PublishedAt ?? DateTime.UtcNow
                : null;
            article.UpdatedAt = DateTime.UtcNow;

            if (!wasPublished && request.IsPublished)
                article.PublishedAt = DateTime.UtcNow;

            await _context.SaveChangesAsync(cancellationToken);
            return Ok(InstructionsController.MapArticle(article));
        }

        [HttpDelete("instructions/{id:guid}")]
        public async Task<IActionResult> DeleteInstruction(Guid id, CancellationToken cancellationToken)
        {
            if (!await this.IsSuperAdminAsync(_context, cancellationToken))
                return Forbid();

            var article = await _context.InstructionArticles.FirstOrDefaultAsync(value => value.Id == id, cancellationToken);
            if (article == null)
                return NotFound();

            _context.InstructionArticles.Remove(article);
            await _context.SaveChangesAsync(cancellationToken);
            return NoContent();
        }

        [HttpPost("instructions/{id:guid}/publish")]
        public async Task<ActionResult<InstructionArticleDto>> PublishInstruction(Guid id, CancellationToken cancellationToken)
        {
            if (!await this.IsSuperAdminAsync(_context, cancellationToken))
                return Forbid();

            var article = await _context.InstructionArticles.FirstOrDefaultAsync(value => value.Id == id, cancellationToken);
            if (article == null)
                return NotFound();

            article.IsPublished = true;
            article.PublishedAt ??= DateTime.UtcNow;
            article.UpdatedAt = DateTime.UtcNow;
            await _context.SaveChangesAsync(cancellationToken);

            return Ok(InstructionsController.MapArticle(article));
        }

        [HttpPost("instructions/{id:guid}/unpublish")]
        public async Task<ActionResult<InstructionArticleDto>> UnpublishInstruction(Guid id, CancellationToken cancellationToken)
        {
            if (!await this.IsSuperAdminAsync(_context, cancellationToken))
                return Forbid();

            var article = await _context.InstructionArticles.FirstOrDefaultAsync(value => value.Id == id, cancellationToken);
            if (article == null)
                return NotFound();

            article.IsPublished = false;
            article.PublishedAt = null;
            article.UpdatedAt = DateTime.UtcNow;
            await _context.SaveChangesAsync(cancellationToken);

            return Ok(InstructionsController.MapArticle(article));
        }

        [HttpPost("instructions/upload-image")]
        [Consumes("multipart/form-data")]
        [RequestSizeLimit(5 * 1024 * 1024)]
        public async Task<ActionResult<UploadInstructionImageResponse>> UploadInstructionImage(
            IFormFile file,
            CancellationToken cancellationToken)
        {
            if (!await this.IsSuperAdminAsync(_context, cancellationToken))
                return Forbid();

            var validation = ValidateInstructionImage(file);
            if (validation != null)
                return BadRequest(new { message = validation });

            var safeFileName = $"{Guid.NewGuid():N}-{ToSafeFileName(file.FileName)}";

            try
            {
                await using var stream = file.OpenReadStream();
                var imageUrl = await _imageKitUploader.UploadAsync(stream, safeFileName, "/instructions", cancellationToken);
                return Ok(new UploadInstructionImageResponse { ImageUrl = imageUrl });
            }
            catch (BusinessRuleException ex)
            {
                return BadRequest(new { message = ex.Message });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Instruction image upload failed for file {FileName}", safeFileName);
                return StatusCode(StatusCodes.Status502BadGateway, new { message = "Failed to upload instruction image." });
            }
        }

        [HttpGet("backup/database")]
        public async Task<IActionResult> DownloadDatabaseBackup(CancellationToken cancellationToken)
        {
            if (!await this.IsSuperAdminAsync(_context, cancellationToken))
                return Forbid();

            var connectionString = _configuration.GetConnectionString("DefaultConnection");
            if (string.IsNullOrWhiteSpace(connectionString))
                return StatusCode(StatusCodes.Status500InternalServerError, new { message = "Database connection is not configured." });

            if (!TryCreatePgDumpOptions(connectionString, out var options))
                return StatusCode(StatusCodes.Status500InternalServerError, new { message = "Database connection string is not supported for backup." });

            var fileName = $"hockeyplanner-backup-{DateTime.UtcNow:yyyyMMdd-HHmmss}.dump";
            var tempPath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}-{fileName}");

            try
            {
                await RunPgDumpAsync(options, tempPath, cancellationToken);

                var fileInfo = new FileInfo(tempPath);
                if (!fileInfo.Exists || fileInfo.Length == 0)
                    return StatusCode(StatusCodes.Status500InternalServerError, new { message = "Database backup file is empty." });

                HttpContext.Response.OnCompleted(() =>
                {
                    DeleteFileIfExists(tempPath);
                    return Task.CompletedTask;
                });

                return PhysicalFile(tempPath, "application/octet-stream", fileName);
            }
            catch (Win32Exception)
            {
                DeleteFileIfExists(tempPath);
                return StatusCode(StatusCodes.Status501NotImplemented, new { message = "pg_dump is not available on this server" });
            }
            catch (OperationCanceledException)
            {
                DeleteFileIfExists(tempPath);
                throw;
            }
            catch
            {
                DeleteFileIfExists(tempPath);
                return StatusCode(StatusCodes.Status500InternalServerError, new { message = "Failed to create database backup." });
            }
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

        private static string? ValidateInstructionRequest(CreateUpdateInstructionArticleRequest request, out string slug)
        {
            slug = request.Slug.Trim().ToLowerInvariant();

            if (string.IsNullOrWhiteSpace(slug))
                return "Slug is required.";

            if (!Regex.IsMatch(slug, "^[a-z0-9]+(?:-[a-z0-9]+)*$"))
                return "Slug must contain lowercase latin letters, numbers and hyphens only.";

            if (slug.Length > 120)
                return "Slug is too long.";

            if (string.IsNullOrWhiteSpace(request.Title))
                return "Title is required.";

            if (request.Title.Trim().Length > 180)
                return "Title is too long.";

            if (request.Summary?.Trim().Length > 500)
                return "Summary is too long.";

            if (string.IsNullOrWhiteSpace(request.Content))
                return "Content is required.";

            if (request.Content.Trim().Length > 12000)
                return "Content is too long.";

            if (request.ImageUrl?.Trim().Length > 1000)
                return "Image URL is too long.";

            return null;
        }

        private static string? ValidateInstructionImage(IFormFile file)
        {
            if (file == null || file.Length == 0)
                return "File is required.";

            if (file.Length > 5 * 1024 * 1024)
                return "File size must not exceed 5 MB.";

            var allowedContentTypes = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "image/jpeg",
                "image/png",
                "image/webp"
            };

            if (!allowedContentTypes.Contains(file.ContentType))
                return "Only JPEG, PNG and WEBP images are supported.";

            var extension = Path.GetExtension(file.FileName);
            var allowedExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                ".jpg",
                ".jpeg",
                ".png",
                ".webp"
            };

            return allowedExtensions.Contains(extension)
                ? null
                : "Only JPEG, PNG and WEBP images are supported.";
        }

        private static string? NormalizeOptional(string? value)
        {
            var trimmed = value?.Trim();
            return string.IsNullOrWhiteSpace(trimmed) ? null : trimmed;
        }

        private static string ToSafeFileName(string fileName)
        {
            var name = Path.GetFileName(fileName);
            foreach (var invalidChar in Path.GetInvalidFileNameChars())
            {
                name = name.Replace(invalidChar, '-');
            }

            return string.IsNullOrWhiteSpace(name) ? "instruction-image" : name;
        }

        private static async Task RunPgDumpAsync(PgDumpOptions options, string outputPath, CancellationToken cancellationToken)
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = "pg_dump",
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                UseShellExecute = false
            };

            startInfo.ArgumentList.Add("-Fc");
            startInfo.ArgumentList.Add("--no-owner");
            startInfo.ArgumentList.Add("--no-privileges");
            startInfo.ArgumentList.Add("--file");
            startInfo.ArgumentList.Add(outputPath);

            AddArgument(startInfo, "--host", options.Host);
            AddArgument(startInfo, "--port", options.Port);
            AddArgument(startInfo, "--username", options.Username);

            startInfo.ArgumentList.Add(options.Database);

            if (!string.IsNullOrWhiteSpace(options.Password))
                startInfo.Environment["PGPASSWORD"] = options.Password;

            if (!string.IsNullOrWhiteSpace(options.SslMode))
                startInfo.Environment["PGSSLMODE"] = options.SslMode.ToLowerInvariant();

            using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("pg_dump failed to start.");
            var standardErrorTask = process.StandardError.ReadToEndAsync(cancellationToken);
            var standardOutputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);

            await process.WaitForExitAsync(cancellationToken);
            await Task.WhenAll(standardErrorTask, standardOutputTask);

            if (process.ExitCode != 0)
                throw new InvalidOperationException("pg_dump failed to create backup.");
        }

        private static void AddArgument(ProcessStartInfo startInfo, string name, string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return;

            startInfo.ArgumentList.Add(name);
            startInfo.ArgumentList.Add(value);
        }

        private static bool TryCreatePgDumpOptions(string connectionString, out PgDumpOptions options)
        {
            options = default!;

            if (TryCreatePgDumpOptionsFromUri(connectionString, out options))
                return true;

            try
            {
                var builder = new DbConnectionStringBuilder { ConnectionString = connectionString };
                var database = GetConnectionValue(builder, "Database");

                if (string.IsNullOrWhiteSpace(database))
                    return false;

                options = new PgDumpOptions(
                    GetConnectionValue(builder, "Host", "Server"),
                    GetConnectionValue(builder, "Port"),
                    database,
                    GetConnectionValue(builder, "Username", "User ID", "UserId", "User"),
                    GetConnectionValue(builder, "Password", "Pwd"),
                    GetConnectionValue(builder, "SSL Mode", "SslMode"));

                return true;
            }
            catch
            {
                options = default!;
                return false;
            }
        }

        private static bool TryCreatePgDumpOptionsFromUri(string connectionString, out PgDumpOptions options)
        {
            options = default!;

            if (!Uri.TryCreate(connectionString, UriKind.Absolute, out var uri) ||
                (uri.Scheme != "postgres" && uri.Scheme != "postgresql"))
            {
                return false;
            }

            var database = Uri.UnescapeDataString(uri.AbsolutePath.TrimStart('/'));
            if (string.IsNullOrWhiteSpace(database))
                return false;

            string? username = null;
            string? password = null;

            if (!string.IsNullOrWhiteSpace(uri.UserInfo))
            {
                var userInfo = uri.UserInfo.Split(':', 2);
                username = Uri.UnescapeDataString(userInfo[0]);
                if (userInfo.Length > 1)
                    password = Uri.UnescapeDataString(userInfo[1]);
            }

            options = new PgDumpOptions(
                uri.Host,
                uri.IsDefaultPort ? null : uri.Port.ToString(),
                database,
                username,
                password,
                GetQueryValue(uri.Query, "sslmode"));

            return true;
        }

        private static string? GetConnectionValue(DbConnectionStringBuilder builder, params string[] keys)
        {
            foreach (var key in keys)
            {
                if (builder.TryGetValue(key, out var value))
                    return Convert.ToString(value);
            }

            return null;
        }

        private static string? GetQueryValue(string query, string key)
        {
            foreach (var part in query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
            {
                var pair = part.Split('=', 2);
                if (pair.Length == 2 && pair[0].Equals(key, StringComparison.OrdinalIgnoreCase))
                    return Uri.UnescapeDataString(pair[1]);
            }

            return null;
        }

        private static void DeleteFileIfExists(string path)
        {
            try
            {
                if (System.IO.File.Exists(path))
                    System.IO.File.Delete(path);
            }
            catch
            {
                // The file is temporary and will be retried by OS cleanup if deletion fails.
            }
        }

        private sealed record PgDumpOptions(
            string? Host,
            string? Port,
            string Database,
            string? Username,
            string? Password,
            string? SslMode);

        private static string ToPreview(string body)
        {
            var trimmed = body.Trim();
            return trimmed.Length <= 220 ? trimmed : trimmed[..220];
        }
    }
}
