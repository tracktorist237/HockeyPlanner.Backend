using HockeyPlanner.Backend.WebAPI.Models.Spbhl;
using HockeyPlanner.Backend.WebAPI.Services;
using Microsoft.AspNetCore.Mvc;

namespace HockeyPlanner.Backend.WebAPI.Controllers
{
    [ApiController]
    [Route("api/spbhl")]
    public class SpbhlController : ControllerBase
    {
        private readonly ISpbhlPlayerSearchService _spbhlPlayerSearchService;
        private readonly ILogger<SpbhlController> _logger;

        public SpbhlController(
            ISpbhlPlayerSearchService spbhlPlayerSearchService,
            ILogger<SpbhlController> logger)
        {
            _spbhlPlayerSearchService = spbhlPlayerSearchService;
            _logger = logger;
        }

        [HttpGet("players")]
        public async Task<ActionResult<SpbhlPlayersSearchResponse>> SearchPlayers(
            [FromQuery] string fullName,
            [FromQuery] string? birthYear,
            [FromQuery] int page = 1,
            CancellationToken cancellationToken = default)
        {
            try
            {
                var result = await _spbhlPlayerSearchService.SearchPlayers(
                    fullName,
                    birthYear,
                    page,
                    cancellationToken);

                return Ok(result);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Ошибка поиска игроков СПБХЛ");
                return StatusCode(StatusCodes.Status502BadGateway, new
                {
                    error = "Не удалось получить данные СПБХЛ"
                });
            }
        }
    }
}
