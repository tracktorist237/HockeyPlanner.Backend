using HockeyPlanner.Backend.Infrastructure.Data;
using HockeyPlanner.Backend.WebAPI.Models.Releases;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace HockeyPlanner.Backend.WebAPI.Controllers
{
    [ApiController]
    [Route("api/releases")]
    public class ReleasesController : ControllerBase
    {
        private readonly AppDbContext _context;

        public ReleasesController(AppDbContext context)
        {
            _context = context;
        }

        [HttpGet]
        public async Task<ActionResult<IReadOnlyCollection<PublicReleaseNoticeDto>>> GetPublishedReleases(CancellationToken cancellationToken)
        {
            var releases = await _context.ReleaseNotices
                .AsNoTracking()
                .Where(release => release.IsPublished)
                .OrderByDescending(release => release.PublishedAt ?? release.CreatedAt)
                .Select(release => new PublicReleaseNoticeDto
                {
                    Id = release.Id,
                    Version = release.Version,
                    Title = release.Title,
                    Body = release.Body,
                    PublishedAt = release.PublishedAt
                })
                .ToListAsync(cancellationToken);

            return Ok(releases);
        }
    }
}
