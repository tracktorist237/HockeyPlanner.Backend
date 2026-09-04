using HockeyPlanner.Backend.Application.Abstractions.Identity;
using HockeyPlanner.Backend.Core.Exceptions;
using HockeyPlanner.Backend.WebAPI.Models.ExternalLeagues;
using HockeyPlanner.Backend.WebAPI.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace HockeyPlanner.Backend.WebAPI.Controllers
{
    [ApiController]
    [Authorize]
    [Route("api/teams/{teamId:guid}/external-links")]
    public sealed class TeamExternalLeagueLinksController(
        ICurrentUser currentUser,
        IExternalLeagueManagementService managementService) : ControllerBase
    {
        [HttpGet]
        public async Task<ActionResult<IReadOnlyCollection<ExternalLeagueLinkDto>>> GetLinks(
            Guid teamId,
            CancellationToken cancellationToken) =>
            await ExecuteAsync(() => managementService.GetLinksAsync(teamId, RequireUserId(), cancellationToken));

        [HttpPost]
        public async Task<ActionResult<ExternalLeagueLinkDto>> CreateLink(
            Guid teamId,
            [FromBody] CreateExternalLeagueLinkRequest request,
            CancellationToken cancellationToken) =>
            await ExecuteAsync(() => managementService.CreateLinkAsync(teamId, RequireUserId(), request, cancellationToken));

        [HttpPost("{linkId:guid}/apply-profile")]
        public async Task<ActionResult<AppliedTeamProfileDto>> ApplyProfile(
            Guid teamId,
            Guid linkId,
            [FromBody] ApplyExternalLeagueProfileRequest request,
            CancellationToken cancellationToken) =>
            await ExecuteAsync(() => managementService.ApplyProfileAsync(
                teamId,
                linkId,
                RequireUserId(),
                request,
                cancellationToken));

        [HttpDelete("{linkId:guid}")]
        public async Task<IActionResult> DeleteLink(
            Guid teamId,
            Guid linkId,
            CancellationToken cancellationToken)
        {
            var result = await ExecuteAsync(async () =>
            {
                await managementService.DeleteLinkAsync(teamId, linkId, RequireUserId(), cancellationToken);
                return true;
            });
            return result.Result ?? NoContent();
        }

        [HttpPost("{linkId:guid}/sync")]
        public async Task<ActionResult<ExternalLeagueSyncResult>> SyncLink(
            Guid teamId,
            Guid linkId,
            CancellationToken cancellationToken) =>
            await ExecuteAsync(() => managementService.SyncLinkAsync(teamId, linkId, RequireUserId(), cancellationToken));

        [HttpPost("sync")]
        public async Task<ActionResult<IReadOnlyCollection<ExternalLeagueSyncResult>>> SyncAll(
            Guid teamId,
            CancellationToken cancellationToken) =>
            await ExecuteAsync(() => managementService.SyncTeamAsync(teamId, RequireUserId(), cancellationToken));

        private Guid RequireUserId()
        {
            return currentUser.UserId ?? throw new UnauthorizedException("Пользователь не авторизован.");
        }

        private async Task<ActionResult<T>> ExecuteAsync<T>(Func<Task<T>> action)
        {
            try
            {
                return Ok(await action());
            }
            catch (UnauthorizedException exception) when (!currentUser.UserId.HasValue)
            {
                return Unauthorized(new { error = exception.Message });
            }
            catch (UnauthorizedException exception)
            {
                return StatusCode(StatusCodes.Status403Forbidden, new { error = exception.Message });
            }
            catch (NotFoundException exception)
            {
                return NotFound(new { error = exception.Message });
            }
            catch (BusinessRuleException exception)
            {
                return BadRequest(new { error = exception.Message });
            }
            catch (HttpRequestException)
            {
                return StatusCode(StatusCodes.Status502BadGateway, new { error = "Не удалось получить данные внешней лиги." });
            }
            catch (OperationCanceledException) when (!HttpContext.RequestAborted.IsCancellationRequested)
            {
                return StatusCode(StatusCodes.Status502BadGateway, new { error = "Не удалось получить данные внешней лиги." });
            }
        }
    }
}
