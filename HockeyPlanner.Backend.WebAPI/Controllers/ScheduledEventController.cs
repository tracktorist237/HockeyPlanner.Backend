using HockeyPlanner.Backend.Application.Abstractions.Identity;
using HockeyPlanner.Backend.Application.Abstractions.Services;
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

        public ScheduledEventController(
            IEventService eventService,
            ICurrentUser currentUser)
        {
            _eventService = eventService;
            _currentUser = currentUser;
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

            var result = await _eventService.CreateEvent(dto, _currentUser.UserId.Value, cancellationToken);
            return CreatedAtAction(nameof(Create), new { id = result }, result);
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

            var result = await _eventService.UpdateEvent(
                dto,
                eventId,
                _currentUser.UserId.Value,
                cancellationToken);
            return CreatedAtAction(nameof(Update), new { id = result }, result);
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

            var result = await _eventService.GetAllEvents(viewerUserId, teamId, cancellationToken);
            return Ok(result);
        }

        [AllowAnonymous]
        [HttpGet]
        [Route("api/events/{id}")]
        public async Task<ActionResult<EventDto>> Get(Guid id, CancellationToken cancellationToken)
        {
            var viewerUserId = _currentUser.UserId;

            var result = await _eventService.GetEvent(id, viewerUserId, cancellationToken);
            return Ok(result);
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

            var conflicts = await _eventService.UpdateAttendance(
                eventId,
                userId,
                dto,
                _currentUser.UserId.Value,
                cancellationToken);
            if (conflicts.Count > 0)
                return Conflict(new { message = "В это время у вас уже есть мероприятие", conflicts });
            return Ok(new { message = "Посещаемость обновлена" });
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

            var result = await _eventService.CreateEventGuest(
                eventId,
                dto,
                _currentUser.UserId.Value,
                cancellationToken);
            return Ok(result);
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

            await _eventService.UpdateEventGuestAttendance(
                eventId,
                guestId,
                dto,
                _currentUser.UserId.Value,
                cancellationToken);
            return Ok(new { message = "Посещаемость гостя обновлена" });
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

            var result = await _eventService.DeleteEvent(
                eventId,
                _currentUser.UserId.Value,
                cancellationToken);
            return result
                ? Ok(new { message = "Мероприятие отменено" })
                : BadRequest(new { message = "Либо у вас нет прав, либо что-то пошло не так" });
        }

    }
}
