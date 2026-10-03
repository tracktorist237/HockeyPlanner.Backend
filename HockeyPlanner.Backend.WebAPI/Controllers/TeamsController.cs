using HockeyPlanner.Backend.Application.Abstractions.Services;
using HockeyPlanner.Backend.Application.Abstractions.Identity;
using Microsoft.AspNetCore.Authorization;
using HockeyPlanner.Backend.WebAPI.Models.Teams;
using HockeyPlanner.Backend.Shared.Models.Teams;
using HockeyPlanner.Backend.WebAPI.Services;
using Microsoft.AspNetCore.Mvc;

namespace HockeyPlanner.Backend.WebAPI.Controllers
{
    [ApiController]
    [Authorize]
    [Route("api/teams")]
    public class TeamsController : ControllerBase
    {
        private readonly ICoreTeamService _coreTeamService;
        private readonly ICurrentUser _currentUser;
        private readonly ITeamNewsService _teamNewsService;
        private readonly ITeamMediaUploadService _teamMediaUploadService;
        private readonly ITeamPwaService _teamPwaService;

        public TeamsController(
            ICoreTeamService coreTeamService,
            ICurrentUser currentUser,
            ITeamNewsService teamNewsService,
            ITeamMediaUploadService teamMediaUploadService,
            ITeamPwaService teamPwaService)
        {
            _coreTeamService = coreTeamService;
            _currentUser = currentUser;
            _teamNewsService = teamNewsService;
            _teamMediaUploadService = teamMediaUploadService;
            _teamPwaService = teamPwaService;
        }

        [AllowAnonymous]
        [HttpGet("{id:guid}/pwa-logo")]
        public async Task<IActionResult> GetPwaLogo(Guid id, CancellationToken cancellationToken)
        {
            var result = await _teamPwaService.GetOriginalLogoAsync(id, cancellationToken);
            if (result == null)
            {
                return NotFound(new { message = "Команда или поддерживаемый логотип команды не найдены." });
            }

            Response.Headers.CacheControl = "public, max-age=3600, must-revalidate";
            Response.Headers.ETag = result.EntityTag;
            return File(result.Content, result.ContentType);
        }

        [HttpGet]
        public async Task<ActionResult<IReadOnlyCollection<TeamDto>>> GetMyTeams()
        {
            if (_currentUser.UserId is not Guid actorUserId || actorUserId == Guid.Empty)
            {
                return Unauthorized();
            }

            return Ok(await _coreTeamService.GetMyTeams(actorUserId, HttpContext.RequestAborted));
        }

        [AllowAnonymous]
        [HttpGet("public")]
        public async Task<ActionResult<IReadOnlyCollection<TeamDto>>> GetPublicTeams()
        {
            return Ok(await _coreTeamService.GetPublicTeams(HttpContext.RequestAborted));
        }

        [AllowAnonymous]
        [HttpGet("{id:guid}")]
        public async Task<ActionResult<TeamDto>> GetTeam(Guid id)
        {
            return Ok(await _coreTeamService.GetTeam(id, _currentUser.IsAuthenticated, _currentUser.UserId, HttpContext.RequestAborted));
        }

        [AllowAnonymous]
        [HttpGet("{id:guid}/members")]
        public async Task<ActionResult<IReadOnlyCollection<TeamMemberDto>>> GetTeamMembers(Guid id)
        {
            return Ok(await _coreTeamService.GetTeamMembers(id, _currentUser.IsAuthenticated, _currentUser.UserId, HttpContext.RequestAborted));
        }

        [AllowAnonymous]
        [HttpGet("{id:guid}/news")]
        public async Task<ActionResult<IReadOnlyCollection<TeamNewsDto>>> GetTeamNews(Guid id)
        {
            return Ok(await _teamNewsService.GetTeamNews(id, _currentUser.IsAuthenticated, _currentUser.UserId, HttpContext.RequestAborted));
        }

        [HttpGet("~/api/news")]
        public async Task<ActionResult<IReadOnlyCollection<TeamNewsDto>>> GetNewsFeed()
        {
            if (_currentUser.UserId is not Guid actorUserId || actorUserId == Guid.Empty)
            {
                return Unauthorized();
            }

            return Ok(await _teamNewsService.GetNewsFeed(actorUserId, HttpContext.RequestAborted));
        }

        [HttpPost("{id:guid}/news")]
        public async Task<ActionResult<TeamNewsDto>> CreateTeamNews(
            Guid id,
            [FromBody] CreateTeamNewsRequest request)
        {
            if (_currentUser.UserId is not Guid actorUserId || actorUserId == Guid.Empty)
            {
                return Unauthorized();
            }

            return Ok(await _teamNewsService.CreateTeamNews(id, actorUserId, request, HttpContext.RequestAborted));
        }

        [HttpPut("{teamId:guid}/news/{newsId:guid}")]
        public async Task<ActionResult<TeamNewsDto>> UpdateTeamNews(
            Guid teamId,
            Guid newsId,
            [FromBody] UpdateTeamNewsRequest request)
        {
            if (_currentUser.UserId is not Guid actorUserId || actorUserId == Guid.Empty)
            {
                return Unauthorized();
            }

            return Ok(await _teamNewsService.UpdateTeamNews(teamId, newsId, actorUserId, request, HttpContext.RequestAborted));
        }

        [HttpDelete("{teamId:guid}/news/{newsId:guid}")]
        public async Task<IActionResult> DeleteTeamNews(Guid teamId, Guid newsId)
        {
            if (_currentUser.UserId is not Guid actorUserId || actorUserId == Guid.Empty)
            {
                return Unauthorized();
            }

            await _teamNewsService.DeleteTeamNews(teamId, newsId, actorUserId, HttpContext.RequestAborted);
            return NoContent();
        }

        [HttpPost("{id:guid}/avatar/upload")]
        [Consumes("multipart/form-data")]
        [RequestSizeLimit(5 * 1024 * 1024)]
        public async Task<ActionResult<TeamDto>> UploadTeamAvatar(
            Guid id,
            IFormFile file,
            CancellationToken cancellationToken)
        {
            if (_currentUser.UserId is not Guid actorUserId || actorUserId == Guid.Empty)
            {
                return Unauthorized();
            }

            return Ok(await _teamMediaUploadService.UploadTeamMedia(id, actorUserId, file, isCover: false, cancellationToken));
        }

        [HttpPost("{id:guid}/cover/upload")]
        [Consumes("multipart/form-data")]
        [RequestSizeLimit(5 * 1024 * 1024)]
        public async Task<ActionResult<TeamDto>> UploadTeamCover(
            Guid id,
            IFormFile file,
            CancellationToken cancellationToken)
        {
            if (_currentUser.UserId is not Guid actorUserId || actorUserId == Guid.Empty)
            {
                return Unauthorized();
            }

            return Ok(await _teamMediaUploadService.UploadTeamMedia(id, actorUserId, file, isCover: true, cancellationToken));
        }

        [HttpPost("{id:guid}/news/upload-image")]
        [Consumes("multipart/form-data")]
        [RequestSizeLimit(5 * 1024 * 1024)]
        public async Task<ActionResult<UploadTeamImageResponse>> UploadTeamNewsImage(
            Guid id,
            IFormFile file,
            CancellationToken cancellationToken)
        {
            if (_currentUser.UserId is not Guid actorUserId || actorUserId == Guid.Empty)
            {
                return Unauthorized();
            }

            return Ok(await _teamMediaUploadService.UploadNewsImage(id, actorUserId, file, cancellationToken));
        }

        [HttpPost]
        public async Task<ActionResult<TeamDto>> CreateTeam([FromBody] CreateTeamRequest request)
        {
            if (_currentUser.UserId is not Guid actorUserId || actorUserId == Guid.Empty)
            {
                return Unauthorized();
            }

            var dto = await _coreTeamService.CreateTeam(actorUserId, request, HttpContext.RequestAborted);
            return CreatedAtAction(nameof(GetTeam), new { id = dto.Id }, dto);
        }

        [HttpPut("{id:guid}")]
        public async Task<ActionResult<TeamDto>> UpdateTeam(Guid id, [FromBody] UpdateTeamRequest request)
        {
            if (_currentUser.UserId is not Guid actorUserId || actorUserId == Guid.Empty)
            {
                return Unauthorized();
            }

            return Ok(await _coreTeamService.UpdateTeam(id, actorUserId, request, HttpContext.RequestAborted));
        }

        [HttpPut("{id:guid}/members/{userId:guid}")]
        public async Task<ActionResult<TeamMemberDto>> UpdateTeamMember(
            Guid id,
            Guid userId,
            [FromBody] UpdateTeamMemberRequest request)
        {
            if (_currentUser.UserId is not Guid actorUserId || actorUserId == Guid.Empty)
            {
                return Unauthorized();
            }

            return Ok(await _coreTeamService.UpdateTeamMember(id, userId, actorUserId, request, HttpContext.RequestAborted));
        }

        [HttpDelete("{id:guid}/members/{userId:guid}")]
        public async Task<IActionResult> RemoveTeamMember(Guid id, Guid userId)
        {
            if (_currentUser.UserId is not Guid actorUserId || actorUserId == Guid.Empty)
            {
                return Unauthorized();
            }

            await _coreTeamService.RemoveTeamMember(id, userId, actorUserId, HttpContext.RequestAborted);
            return NoContent();
        }

        [HttpPost("join-by-code")]
        public async Task<ActionResult<TeamDto>> JoinByCode([FromBody] JoinTeamByCodeRequest request)
        {
            if (_currentUser.UserId is not Guid actorUserId || actorUserId == Guid.Empty)
            {
                return Unauthorized();
            }

            return Ok(await _coreTeamService.JoinByCode(actorUserId, request, HttpContext.RequestAborted));
        }

        [HttpPost("{id:guid}/join-public")]
        public async Task<ActionResult<TeamDto>> JoinPublic(Guid id, [FromQuery] int? teamJerseyNumber)
        {
            if (_currentUser.UserId is not Guid actorUserId || actorUserId == Guid.Empty)
            {
                return Unauthorized();
            }

            return Ok(await _coreTeamService.JoinPublic(id, actorUserId, teamJerseyNumber, HttpContext.RequestAborted));
        }

        [HttpDelete("{id:guid}/members/me")]
        public async Task<IActionResult> LeaveTeam(Guid id)
        {
            if (_currentUser.UserId is not Guid actorUserId || actorUserId == Guid.Empty)
            {
                return Unauthorized();
            }

            await _coreTeamService.LeaveTeam(id, actorUserId, HttpContext.RequestAborted);
            return NoContent();
        }

        [HttpPut("{id:guid}/members/me/number")]
        public async Task<ActionResult<TeamDto>> UpdateMyTeamJerseyNumber(
            Guid id,
            [FromBody] UpdateMyTeamJerseyNumberRequest request)
        {
            if (_currentUser.UserId is not Guid actorUserId || actorUserId == Guid.Empty)
            {
                return Unauthorized();
            }

            return Ok(await _coreTeamService.UpdateMyTeamJerseyNumber(id, actorUserId, request, HttpContext.RequestAborted));
        }

    }
}
