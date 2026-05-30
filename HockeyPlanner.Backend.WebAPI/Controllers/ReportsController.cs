using HockeyPlanner.Backend.Core.Entities;
using HockeyPlanner.Backend.Core.Enums;
using HockeyPlanner.Backend.Infrastructure.Data;
using HockeyPlanner.Backend.WebAPI.Extensions;
using HockeyPlanner.Backend.WebAPI.Models.Admin;
using Microsoft.AspNetCore.Mvc;

namespace HockeyPlanner.Backend.WebAPI.Controllers
{
    [ApiController]
    [Route("api/reports")]
    public class ReportsController : ControllerBase
    {
        private readonly AppDbContext _context;

        public ReportsController(AppDbContext context)
        {
            _context = context;
        }

        [HttpPost]
        public async Task<ActionResult<AppReportDto>> CreateReport([FromBody] CreateAppReportRequest request, CancellationToken cancellationToken)
        {
            var title = request.Title.Trim();
            var message = request.Message.Trim();

            if (string.IsNullOrWhiteSpace(title))
                return BadRequest(new { message = "Тема обращения обязательна." });

            if (string.IsNullOrWhiteSpace(message))
                return BadRequest(new { message = "Описание обращения обязательно." });

            if (title.Length > 180)
                return BadRequest(new { message = "Тема обращения слишком длинная." });

            if (message.Length > 4000)
                return BadRequest(new { message = "Описание обращения слишком длинное." });

            var now = DateTime.UtcNow;
            var report = new AppReport
            {
                UserId = User.GetUserId(),
                Type = Enum.IsDefined(request.Type) ? request.Type : AppReportType.Other,
                Severity = request.Severity.HasValue && Enum.IsDefined(request.Severity.Value)
                    ? request.Severity.Value
                    : AppReportSeverity.Medium,
                Status = AppReportStatus.New,
                Title = title,
                Message = message,
                Route = TrimOrNull(request.Route, 500),
                EntityType = TrimOrNull(request.EntityType, 100),
                EntityId = request.EntityId,
                AppVersion = TrimOrNull(request.AppVersion, 50),
                Platform = TrimOrNull(request.Platform, 100),
                UserAgent = TrimOrNull(request.UserAgent ?? Request.Headers.UserAgent.ToString(), 500),
                CreatedAt = now,
                UpdatedAt = now,
            };

            await _context.AppReports.AddAsync(report, cancellationToken);
            await _context.SaveChangesAsync(cancellationToken);

            return CreatedAtAction(nameof(CreateReport), new { id = report.Id }, MapReport(report));
        }

        private static string? TrimOrNull(string? value, int maxLength)
        {
            var trimmed = value?.Trim();
            if (string.IsNullOrWhiteSpace(trimmed))
                return null;

            return trimmed.Length <= maxLength ? trimmed : trimmed[..maxLength];
        }

        internal static AppReportDto MapReport(AppReport report)
        {
            return new AppReportDto
            {
                Id = report.Id,
                UserId = report.UserId,
                UserName = report.User == null ? null : report.User.FullName,
                UserEmail = report.User?.Email,
                Type = report.Type,
                Status = report.Status,
                Severity = report.Severity,
                Title = report.Title,
                Message = report.Message,
                Route = report.Route,
                EntityType = report.EntityType,
                EntityId = report.EntityId,
                AppVersion = report.AppVersion,
                Platform = report.Platform,
                UserAgent = report.UserAgent,
                CreatedAt = report.CreatedAt,
                UpdatedAt = report.UpdatedAt,
                ResolvedAt = report.ResolvedAt,
            };
        }
    }
}
