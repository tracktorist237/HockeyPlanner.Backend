using HockeyPlanner.Backend.Application.Abstractions.Services;
using HockeyPlanner.Backend.Application.Abstractions.Identity;
using HockeyPlanner.Backend.Core.Exceptions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace HockeyPlanner.Backend.WebAPI.Controllers
{
    [ApiController]
    public class PlayersController : ControllerBase
    {
        private readonly IPlayerService _playerService;
        private readonly ICurrentUser _currentUser;

        public PlayersController(IPlayerService playerService, ICurrentUser currentUser)
        {
            _playerService = playerService;
            _currentUser = currentUser;
        }

        [Authorize]
        [HttpDelete]
        [Route("api/players")]
        public async Task<IActionResult> RemovePlayerById(
            [FromQuery] Guid playerId,
            CancellationToken cancellationToken)
        {
            if (!_currentUser.UserId.HasValue)
                return Unauthorized(new { error = "Не удалось определить пользователя" });

            try
            {
                var result = await _playerService.RemovePlayerById(
                    playerId,
                    _currentUser.UserId.Value,
                    cancellationToken);

                return CreatedAtAction(nameof(RemovePlayerById), new { id = result }, result);
            }
            catch (NotFoundException ex)
            {
                return NotFound(new { error = ex.Message });
            }
            catch (UnauthorizedException ex)
            {
                return StatusCode(StatusCodes.Status403Forbidden, new { error = ex.Message });
            }
        }
    }
}
