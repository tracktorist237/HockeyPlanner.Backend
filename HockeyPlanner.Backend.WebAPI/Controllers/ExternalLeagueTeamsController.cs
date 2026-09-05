using HockeyPlanner.Backend.Application.Abstractions.Identity;
using HockeyPlanner.Backend.Core.Enums;
using HockeyPlanner.Backend.Core.Exceptions;
using HockeyPlanner.Backend.WebAPI.Models.ExternalLeagues;
using HockeyPlanner.Backend.WebAPI.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace HockeyPlanner.Backend.WebAPI.Controllers
{
    [ApiController]
    [Authorize]
    [Route("api/external-leagues/{provider}/teams")]
    public sealed class ExternalLeagueTeamsController(
        ICurrentUser currentUser,
        IExternalLeagueManagementService managementService) : ControllerBase
    {
        [HttpGet("search")]
        public async Task<ActionResult<IReadOnlyCollection<ExternalTeamSearchItem>>> Search(
            ExternalLeagueProvider provider,
            [FromQuery] string title,
            CancellationToken cancellationToken)
        {
            if (!currentUser.UserId.HasValue)
            {
                return Unauthorized();
            }

            try
            {
                return Ok(await managementService.SearchTeamsAsync(provider, title, cancellationToken));
            }
            catch (BusinessRuleException exception)
            {
                return BadRequest(new { error = exception.Message });
            }
            catch (HttpRequestException)
            {
                return StatusCode(StatusCodes.Status502BadGateway, new { error = "Не удалось получить данные внешней лиги." });
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                return StatusCode(StatusCodes.Status502BadGateway, new { error = "Не удалось получить данные внешней лиги." });
            }
        }
    }
}
