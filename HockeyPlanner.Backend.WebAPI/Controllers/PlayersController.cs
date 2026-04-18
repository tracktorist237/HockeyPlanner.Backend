using HockeyPlanner.Backend.Application.Abstractions.Services;
using Microsoft.AspNetCore.Mvc;

namespace HockeyPlanner.Backend.WebAPI.Controllers
{
    [ApiController]
    public class PlayersController : ControllerBase
    {
        private readonly IPlayerService _playerService;

        public PlayersController(IPlayerService playerService)
        {
            _playerService = playerService;
        }

        [HttpDelete]
        [Route("api/players")]
        public async Task<IActionResult> RemovePlayerById([FromQuery] Guid playerId)
        {
            var result = await _playerService.RemovePlayerById(playerId);

            return CreatedAtAction(nameof(RemovePlayerById), new { id = result }, result);
        }
    }
}
