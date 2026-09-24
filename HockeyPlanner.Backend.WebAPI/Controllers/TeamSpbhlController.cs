using HockeyPlanner.Backend.Application.Abstractions.Identity;
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

            return Ok(await _managementService.GetStatusAsync(teamId, actorUserId.Value, cancellationToken));
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

            return Ok(await _managementService.SearchTeamsAsync(teamId, actorUserId.Value, title, cancellationToken));
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

            return Ok(await _managementService.BindAsync(teamId, actorUserId.Value, request, cancellationToken));
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

            return Ok(await _managementService.UnbindAsync(teamId, actorUserId.Value, cancellationToken));
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

            return Ok(await _managementService.SyncNowAsync(teamId, actorUserId.Value, cancellationToken));
        }
    }
}
