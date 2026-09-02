using HockeyPlanner.Backend.Application.Abstractions.Identity;
using HockeyPlanner.Backend.Application.Abstractions.Services;
using HockeyPlanner.Backend.Core.Exceptions;
using HockeyPlanner.Backend.Shared.Models.Events;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace HockeyPlanner.Backend.WebAPI.Controllers
{
    [ApiController]
    public class ScheduledEventController : ControllerBase
    {
        private readonly IEventService _eventService;
        private readonly ICurrentUser _currentUser;
        private readonly ILogger<ScheduledEventController> _logger;

        public ScheduledEventController(
            IEventService eventService,
            ICurrentUser currentUser,
            ILogger<ScheduledEventController> logger)
        {
            _eventService = eventService;
            _currentUser = currentUser;
            _logger = logger;
        }

        [Authorize]
        [HttpPost]
        [Route("api/events")]
        public async Task<ActionResult<Guid>> Create(
            [FromBody] CreateEventDto dto,
            [FromQuery] Guid currentUserId,
            CancellationToken cancellationToken)
        {
            if (!_currentUser.UserId.HasValue)
                return Unauthorized(new { error = "Не удалось определить пользователя" });

            try
            {
                var result = await _eventService.CreateEvent(dto, _currentUser.UserId.Value, cancellationToken);
                return CreatedAtAction(nameof(Create), new { id = result }, result);
            }
            catch (NotFoundException ex)
            {
                return NotFound(new { error = ex.Message });
            }
            catch (UnauthorizedException ex)
            {
                return StatusCode(StatusCodes.Status403Forbidden, new { error = ex.Message });
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

        [Authorize]
        [HttpPut]
        [Route("api/events")]
        public async Task<ActionResult<Guid>> Update(
            [FromBody] UpdateEventDto dto,
            [FromQuery] Guid currentUserId,
            Guid eventId,
            CancellationToken cancellationToken)
        {
            if (!_currentUser.UserId.HasValue)
                return Unauthorized(new { error = "Не удалось определить пользователя" });

            try
            {
                var result = await _eventService.UpdateEvent(
                    dto,
                    eventId,
                    _currentUser.UserId.Value,
                    cancellationToken);
                return CreatedAtAction(nameof(Update), new { id = result }, result);
            }
            catch (NotFoundException ex)
            {
                return NotFound(new { error = ex.Message });
            }
            catch (UnauthorizedException ex)
            {
                return StatusCode(StatusCodes.Status403Forbidden, new { error = ex.Message });
            }
            catch (BusinessRuleException ex)
            {
                return BadRequest(new { error = ex.Message });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Ошибка обновления мероприятия");
                return StatusCode(500, new { error = "Внутренняя ошибка сервера" });
            }
        }

        [AllowAnonymous]
        [HttpGet]
        [Route("api/events")]
        public async Task<ActionResult<EventListDto>> GetAll(
            [FromQuery] Guid? currentUserId,
            [FromQuery] Guid? teamId,
            CancellationToken cancellationToken)
        {
            var viewerUserId = _currentUser.UserId;

            try
            {
                var result = await _eventService.GetAllEvents(viewerUserId, teamId, cancellationToken);
                return Ok(result);
            }
            catch (NotFoundException ex)
            {
                return NotFound(new { error = ex.Message });
            }
            catch (UnauthorizedException ex)
            {
                return AccessDenied(viewerUserId, ex.Message);
            }
        }

        [AllowAnonymous]
        [HttpGet]
        [Route("api/events/{id}")]
        public async Task<ActionResult<EventDto>> Get(Guid id, CancellationToken cancellationToken)
        {
            var viewerUserId = _currentUser.UserId;

            try
            {
                var result = await _eventService.GetEvent(id, viewerUserId, cancellationToken);
                return Ok(result);
            }
            catch (NotFoundException ex)
            {
                return NotFound(new { error = ex.Message });
            }
            catch (UnauthorizedException ex)
            {
                return AccessDenied(viewerUserId, ex.Message);
            }
        }

        [Authorize]
        [HttpPost("api/events/{eventId}/attendance/{userId}")]
        public async Task<IActionResult> UpdateAttendance(
            Guid eventId,
            Guid userId,
            [FromQuery] Guid? currentUserId,
            [FromBody] UpdateAttendanceRequest dto,
            CancellationToken cancellationToken)
        {
            if (!_currentUser.UserId.HasValue)
                return Unauthorized(new { error = "Не удалось определить пользователя" });

            try
            {
                await _eventService.UpdateAttendance(
                    eventId,
                    userId,
                    dto,
                    _currentUser.UserId.Value,
                    cancellationToken);
                return Ok(new { message = "Посещаемость обновлена" });
            }
            catch (NotFoundException ex)
            {
                return NotFound(new { error = ex.Message });
            }
            catch (UnauthorizedException ex)
            {
                return StatusCode(StatusCodes.Status403Forbidden, new { error = ex.Message });
            }
            catch (BusinessRuleException ex)
            {
                return BadRequest(new { error = ex.Message });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Ошибка обновления посещаемости");
                return StatusCode(500, new { error = "Внутренняя ошибка сервера" });
            }
        }

        [Authorize]
        [HttpPost("api/events/{eventId}/guests")]
        public async Task<ActionResult<AttendanceLookUpDto>> CreateEventGuest(
            Guid eventId,
            [FromQuery] Guid currentUserId,
            [FromBody] CreateEventGuestRequest dto,
            CancellationToken cancellationToken)
        {
            if (!_currentUser.UserId.HasValue)
                return Unauthorized(new { error = "Не удалось определить пользователя" });

            try
            {
                var result = await _eventService.CreateEventGuest(
                    eventId,
                    dto,
                    _currentUser.UserId.Value,
                    cancellationToken);
                return Ok(result);
            }
            catch (NotFoundException ex)
            {
                return NotFound(new { error = ex.Message });
            }
            catch (UnauthorizedException ex)
            {
                return StatusCode(StatusCodes.Status403Forbidden, new { error = ex.Message });
            }
            catch (BusinessRuleException ex)
            {
                return BadRequest(new { error = ex.Message });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Ошибка добавления гостя");
                return StatusCode(500, new { error = "Внутренняя ошибка сервера" });
            }
        }

        [Authorize]
        [HttpPost("api/events/{eventId}/guests/{guestId}/attendance")]
        public async Task<IActionResult> UpdateEventGuestAttendance(
            Guid eventId,
            Guid guestId,
            [FromQuery] Guid currentUserId,
            [FromBody] UpdateAttendanceRequest dto,
            CancellationToken cancellationToken)
        {
            if (!_currentUser.UserId.HasValue)
                return Unauthorized(new { error = "Не удалось определить пользователя" });

            try
            {
                await _eventService.UpdateEventGuestAttendance(
                    eventId,
                    guestId,
                    dto,
                    _currentUser.UserId.Value,
                    cancellationToken);
                return Ok(new { message = "Посещаемость гостя обновлена" });
            }
            catch (NotFoundException ex)
            {
                return NotFound(new { error = ex.Message });
            }
            catch (UnauthorizedException ex)
            {
                return StatusCode(StatusCodes.Status403Forbidden, new { error = ex.Message });
            }
            catch (BusinessRuleException ex)
            {
                return BadRequest(new { error = ex.Message });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Ошибка обновления посещаемости гостя");
                return StatusCode(500, new { error = "Внутренняя ошибка сервера" });
            }
        }

        [Authorize]
        [HttpDelete("api/events/")]
        public async Task<IActionResult> Delete(
            [FromQuery] Guid currentUserId,
            Guid eventId,
            CancellationToken cancellationToken)
        {
            if (!_currentUser.UserId.HasValue)
                return Unauthorized(new { error = "Не удалось определить пользователя" });

            try
            {
                var result = await _eventService.DeleteEvent(
                    eventId,
                    _currentUser.UserId.Value,
                    cancellationToken);
                return result
                    ? Ok(new { message = "Мероприятие отменено" })
                    : BadRequest(new { message = "Либо у вас нет прав, либо что-то пошло не так" });
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

        private ActionResult AccessDenied(Guid? viewerUserId, string message) =>
            viewerUserId.HasValue
                ? StatusCode(StatusCodes.Status403Forbidden, new { error = message })
                : Unauthorized(new { error = message });
    }
}
