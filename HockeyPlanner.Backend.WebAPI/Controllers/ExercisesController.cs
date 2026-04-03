using HockeyPlanner.Backend.Application.Abstractions.Services;
using HockeyPlanner.Backend.Core.Exceptions;
using HockeyPlanner.Backend.Shared.Models.Exercises;
using Microsoft.AspNetCore.Mvc;

namespace HockeyPlanner.Backend.WebAPI.Controllers
{
    [ApiController]
    [Route("api/exercises")]
    public class ExercisesController : ControllerBase
    {
        private readonly IExerciseService _exerciseService;
        private readonly ILogger<ExercisesController> _logger;

        public ExercisesController(IExerciseService exerciseService, ILogger<ExercisesController> logger)
        {
            _exerciseService = exerciseService;
            _logger = logger;
        }

        [HttpGet]
        public async Task<ActionResult<IReadOnlyCollection<ExerciseDto>>> GetAll()
        {
            var items = await _exerciseService.GetAll();
            return Ok(items);
        }

        [HttpPost]
        public async Task<ActionResult<ExerciseDto>> Create([FromBody] CreateExerciseDto dto, [FromQuery] Guid currentUserId)
        {
            try
            {
                var item = await _exerciseService.Create(dto, currentUserId);
                return Ok(item);
            }
            catch (NotFoundException ex)
            {
                return NotFound(new { error = ex.Message });
            }
            catch (UnauthorizedException ex)
            {
                return Unauthorized(new { error = ex.Message });
            }
            catch (BusinessRuleException ex)
            {
                return BadRequest(new { error = ex.Message });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Ошибка создания упражнения");
                return StatusCode(500, new { error = "Внутренняя ошибка сервера" });
            }
        }
    }
}

