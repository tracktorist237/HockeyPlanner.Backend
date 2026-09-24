using HockeyPlanner.Backend.Application.Abstractions.Identity;
using HockeyPlanner.Backend.Core.Enums;
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

            return Ok(await managementService.SearchTeamsAsync(provider, title, cancellationToken));
        }
    }
}
