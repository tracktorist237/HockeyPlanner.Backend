using HockeyPlanner.Backend.Application.Abstractions.Services;
using HockeyPlanner.Backend.Application.Abstractions.Identity;
using HockeyPlanner.Backend.Core.Entities;
using HockeyPlanner.Backend.Core.Exceptions;
using HockeyPlanner.Backend.Shared.Models.Lines;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace HockeyPlanner.Backend.WebAPI.Controllers
{
    [ApiController]
    public class LinesController : ControllerBase
    {
        private readonly ILineService _lineService;
        private readonly ICurrentUser _currentUser;

        public LinesController(ILineService lineService, ICurrentUser currentUser)
        {
            _lineService = lineService;
            _currentUser = currentUser;
        }

        [AllowAnonymous]
        [HttpGet]
        [Route("api/lines")]
        public async Task<IActionResult> GetRosterByEvent(
            [FromQuery] Guid eventId,
            CancellationToken cancellationToken)
        {
            var viewerUserId = _currentUser.UserId;

            try
            {
                var result = await _lineService.GetRosterByEvent(
                    eventId,
                    viewerUserId,
                    cancellationToken);

                return CreatedAtAction(nameof(GetRosterByEvent), new { id = result }, result);
            }
            catch (NotFoundException ex)
            {
                return NotFound(new { error = ex.Message });
            }
            catch (UnauthorizedException ex)
            {
                return AccessDenied(viewerUserId, ex.Message);
            }
            catch (BusinessRuleException ex)
            {
                return BadRequest(new { error = ex.Message });
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { error = "Внутренняя ошибка сервера" });
            }
        }

        [Authorize]
        [HttpPost]
        [Route("api/lines")]
        public async Task<IActionResult> CreateRoster(
            [FromBody] CreateUpdateRosterRequest request,
            [FromQuery] Guid currentUserId,
            CancellationToken cancellationToken)
        {
            if (!_currentUser.UserId.HasValue)
                return Unauthorized(new { error = "Не удалось определить пользователя" });

            try
            {
                var result = await _lineService.CreateRoster(
                    request,
                    _currentUser.UserId.Value,
                    cancellationToken);

                return CreatedAtAction(nameof(CreateRoster), new { id = result }, result);
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
                return StatusCode(500, new { error = "Внутренняя ошибка сервера" });
            }
        }

        [Authorize]
        [HttpPut]
        [Route("api/lines")]
        public async Task<IActionResult> UpdateRoster(
            [FromBody] CreateUpdateRosterRequest request,
            Guid currentUserId,
            CancellationToken cancellationToken)
        {
            if (!_currentUser.UserId.HasValue)
                return Unauthorized(new { error = "Не удалось определить пользователя" });

            try
            {
                var result = await _lineService.UpdateRoster(
                    request,
                    _currentUser.UserId.Value,
                    cancellationToken);

                return CreatedAtAction(nameof(UpdateRoster), new { id = result }, result);
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
                return StatusCode(500, new { error = "Внутренняя ошибка сервера" });
            }
        }

        [Authorize]
        [HttpDelete]
        [Route("api/lines")]
        public async Task<IActionResult> RemoveRosterByEvent(
            [FromQuery] Guid eventId,
            Guid currentUserId,
            CancellationToken cancellationToken)
        {
            if (!_currentUser.UserId.HasValue)
                return Unauthorized(new { error = "Не удалось определить пользователя" });

            try
            {
                var result = await _lineService.RemoveRosterByEvent(
                    eventId,
                    _currentUser.UserId.Value,
                    cancellationToken);

                return CreatedAtAction(nameof(RemoveRosterByEvent), new { id = result }, result);
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
                return StatusCode(500, new { error = "Внутренняя ошибка сервера" });
            }
        }

        private ActionResult AccessDenied(Guid? viewerUserId, string message) =>
            viewerUserId.HasValue
                ? StatusCode(StatusCodes.Status403Forbidden, new { error = message })
                : Unauthorized(new { error = message });
    }
}
