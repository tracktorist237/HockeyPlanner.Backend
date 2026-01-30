using HockeyPlanner.Backend.Application.Abstractions.Services;
using HockeyPlanner.Backend.Core.Exceptions;
using HockeyPlanner.Backend.Shared.Models.Events;
using Microsoft.AspNetCore.Mvc;

namespace HockeyPlanner.Backend.WebAPI.Controllers
{
    [ApiController]
    public class ScheduledEventController : ControllerBase
    {
        private readonly IEventService _eventService;
        private readonly ILogger<ScheduledEventController> _logger;

        public ScheduledEventController(IEventService eventService, ILogger<ScheduledEventController> logger)
        {
            _eventService = eventService;
            _logger = logger;
        }

        [HttpPost]
        [Route("api/events")]
        public async Task<ActionResult<Guid>> Create([FromBody] CreateEventDto dto)
        {
            try
            {
                var currentUserId = GetCurrentUserId();
                var result = await _eventService.CreateEvent(dto, currentUserId);
                return CreatedAtAction(nameof(Create), new { id = result }, result);
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
                _logger.LogError(ex, "Ошибка создания мероприятия");
                return StatusCode(500, new { error = "Внутренняя ошибка сервера" });
            }
        }

        [HttpGet]
        [Route("api/events")]
        public async Task<ActionResult<EventListDto>> GetAll()
        {
            try
            {
                var result = await _eventService.GetAllEvents();
                return Ok(result);
            }
            catch (NotFoundException ex)
            {
                return NotFound(new { error = ex.Message });
            }
        }

        [HttpGet]
        [Route("api/events/{id}")]
        public async Task<ActionResult<EventDto>> Get(Guid id)
        {
            try
            {
                var result = await _eventService.GetEvent(id);
                return Ok(result);
            }
            catch (NotFoundException ex)
            {
                return NotFound(new { error = ex.Message });
            }
        }

        [HttpPost("api/events/{eventId}/attendance/{userId}")]
        public async Task<IActionResult> UpdateAttendance(
            Guid eventId,
            Guid userId,
            [FromBody] UpdateAttendanceRequest dto)
        {
            try
            {
                await _eventService.UpdateAttendance(eventId, userId, dto);
                return Ok(new { message = "Посещаемость обновлена" });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Ошибка обновления посещаемости");
                return BadRequest(new { error = ex.Message });
            }
        }

        [HttpDelete("api/events/{eventId}")]
        public async Task<IActionResult> Delete(Guid eventId)
        {
            try
            {
                var currentUserId = GetCurrentUserId();
                await _eventService.CancelEvent(eventId, currentUserId);
                return Ok(new { message = "Мероприятие отменено" });
            }
            catch (NotFoundException ex)
            {
                return NotFound(new { error = ex.Message });
            }
            catch (UnauthorizedException ex)
            {
                return Unauthorized(new { error = ex.Message });
            }
        }

        private Guid GetCurrentUserId()
        {
            // Для разработки
            return Guid.Parse("11111111-1111-1111-1111-111111111111");
        }
    }
}
