using HockeyPlanner.Backend.WebAPI.Services;
using Microsoft.AspNetCore.Mvc;

namespace HockeyPlanner.Backend.WebAPI.Controllers
{
    [ApiController]
    [Route("api/pwa/teams/{teamId:guid}")]
    public sealed class PwaController : ControllerBase
    {
        private readonly ITeamPwaService _teamPwaService;

        public PwaController(ITeamPwaService teamPwaService)
        {
            _teamPwaService = teamPwaService;
        }

        [HttpGet("manifest.webmanifest")]
        public async Task<IActionResult> GetManifest(
            Guid teamId,
            [FromQuery(Name = "name")] string? appName,
            CancellationToken cancellationToken)
        {
            var result = await _teamPwaService.GetManifestAsync(teamId, appName, cancellationToken);
            if (result == null)
            {
                return NotFound(new { message = "Команда или логотип команды не найдены." });
            }

            Response.Headers.CacheControl = "public, max-age=300, must-revalidate";
            Response.Headers.ETag = result.EntityTag;
            return File(result.Content, "application/manifest+json; charset=utf-8");
        }

        [HttpGet("icons/{size:int}.png")]
        public async Task<IActionResult> GetIcon(
            Guid teamId,
            int size,
            CancellationToken cancellationToken)
        {
            var result = await _teamPwaService.GetIconAsync(teamId, size, cancellationToken);
            if (result == null)
            {
                return NotFound(new { message = "Команда, логотип или размер иконки не найдены." });
            }

            Response.Headers.CacheControl = "public, max-age=3600, must-revalidate";
            Response.Headers.ETag = result.EntityTag;
            return File(result.Content, result.ContentType);
        }
    }
}
