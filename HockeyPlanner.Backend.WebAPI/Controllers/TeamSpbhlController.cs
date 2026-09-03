using HockeyPlanner.Backend.Application.Abstractions.Identity;
using HockeyPlanner.Backend.Core.Exceptions;
using HockeyPlanner.Backend.WebAPI.Models.Spbhl;
using HockeyPlanner.Backend.WebAPI.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace HockeyPlanner.Backend.WebAPI.Controllers
{
    [ApiController]
    [Authorize]
    [Route("api/teams/{teamId:guid}/spbhl")]
    public sealed class TeamSpbhlController : ControllerBase
    {
        private const string UpstreamError = "Не удалось получить данные СПбХЛ.";
        private readonly ICurrentUser _currentUser;
        private readonly ISpbhlTeamManagementService _managementService;

        public TeamSpbhlController(
            ICurrentUser currentUser,
            ISpbhlTeamManagementService managementService)
        {
            _currentUser = currentUser;
            _managementService = managementService;
        }

        [HttpGet]
        public async Task<ActionResult<SpbhlTeamLinkStatusDto>> GetStatus(
            Guid teamId,
            CancellationToken cancellationToken)
        {
            var actorUserId = _currentUser.UserId;
            if (!actorUserId.HasValue)
            {
                return Unauthorized();
            }

            try
            {
                return Ok(await _managementService.GetStatusAsync(teamId, actorUserId.Value, cancellationToken));
            }
            catch (NotFoundException exception)
            {
                return NotFound(new { error = exception.Message });
            }
            catch (UnauthorizedException exception)
            {
                return StatusCode(StatusCodes.Status403Forbidden, new { error = exception.Message });
            }
        }

        [HttpGet("search")]
        public async Task<ActionResult<IReadOnlyCollection<SpbhlTeamSearchItem>>> Search(
            Guid teamId,
            [FromQuery] string title,
            CancellationToken cancellationToken)
        {
            var actorUserId = _currentUser.UserId;
            if (!actorUserId.HasValue)
            {
                return Unauthorized();
            }

            try
            {
                return Ok(await _managementService.SearchTeamsAsync(teamId, actorUserId.Value, title, cancellationToken));
            }
            catch (NotFoundException exception)
            {
                return NotFound(new { error = exception.Message });
            }
            catch (UnauthorizedException exception)
            {
                return StatusCode(StatusCodes.Status403Forbidden, new { error = exception.Message });
            }
            catch (BusinessRuleException exception)
            {
                return BadRequest(new { error = exception.Message });
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                return StatusCode(StatusCodes.Status502BadGateway, new { error = UpstreamError });
            }
            catch (HttpRequestException)
            {
                return StatusCode(StatusCodes.Status502BadGateway, new { error = UpstreamError });
            }
        }

        [HttpPost("link")]
        public async Task<ActionResult<SpbhlTeamBindResult>> Bind(
            Guid teamId,
            [FromBody] BindSpbhlTeamRequest request,
            CancellationToken cancellationToken)
        {
            var actorUserId = _currentUser.UserId;
            if (!actorUserId.HasValue)
            {
                return Unauthorized();
            }

            try
            {
                return Ok(await _managementService.BindAsync(teamId, actorUserId.Value, request, cancellationToken));
            }
            catch (NotFoundException exception)
            {
                return NotFound(new { error = exception.Message });
            }
            catch (UnauthorizedException exception)
            {
                return StatusCode(StatusCodes.Status403Forbidden, new { error = exception.Message });
            }
            catch (BusinessRuleException exception)
            {
                return BadRequest(new { error = exception.Message });
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                return StatusCode(StatusCodes.Status502BadGateway, new { error = UpstreamError });
            }
            catch (HttpRequestException)
            {
                return StatusCode(StatusCodes.Status502BadGateway, new { error = UpstreamError });
            }
        }

        [HttpDelete]
        public async Task<ActionResult<SpbhlTeamLinkStatusDto>> Unbind(
            Guid teamId,
            CancellationToken cancellationToken)
        {
            var actorUserId = _currentUser.UserId;
            if (!actorUserId.HasValue)
            {
                return Unauthorized();
            }

            try
            {
                return Ok(await _managementService.UnbindAsync(teamId, actorUserId.Value, cancellationToken));
            }
            catch (NotFoundException exception)
            {
                return NotFound(new { error = exception.Message });
            }
            catch (UnauthorizedException exception)
            {
                return StatusCode(StatusCodes.Status403Forbidden, new { error = exception.Message });
            }
        }

        [HttpPost("sync")]
        public async Task<ActionResult<SpbhlTeamSyncResult>> Sync(
            Guid teamId,
            CancellationToken cancellationToken)
        {
            var actorUserId = _currentUser.UserId;
            if (!actorUserId.HasValue)
            {
                return Unauthorized();
            }

            try
            {
                return Ok(await _managementService.SyncNowAsync(teamId, actorUserId.Value, cancellationToken));
            }
            catch (NotFoundException exception)
            {
                return NotFound(new { error = exception.Message });
            }
            catch (UnauthorizedException exception)
            {
                return StatusCode(StatusCodes.Status403Forbidden, new { error = exception.Message });
            }
            catch (BusinessRuleException exception)
            {
                return BadRequest(new { error = exception.Message });
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                return StatusCode(StatusCodes.Status502BadGateway, new { error = UpstreamError });
            }
            catch (HttpRequestException)
            {
                return StatusCode(StatusCodes.Status502BadGateway, new { error = UpstreamError });
            }
        }
    }
}
