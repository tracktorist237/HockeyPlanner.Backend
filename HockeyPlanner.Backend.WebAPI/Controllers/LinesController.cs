using HockeyPlanner.Backend.Application.Abstractions.Services;
using HockeyPlanner.Backend.Application.Abstractions.Identity;
using HockeyPlanner.Backend.Shared.Models.Lines;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

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

            var result = await _lineService.GetRosterByEvent(
                eventId,
                viewerUserId,
                cancellationToken);

            return CreatedAtAction(nameof(GetRosterByEvent), new { id = result }, result);
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

            var result = await _lineService.CreateRoster(
                request,
                _currentUser.UserId.Value,
                cancellationToken);

            return CreatedAtAction(nameof(CreateRoster), new { id = result }, result);
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

            var result = await _lineService.UpdateRoster(
                request,
                _currentUser.UserId.Value,
                cancellationToken);

            return CreatedAtAction(nameof(UpdateRoster), new { id = result }, result);
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

            var result = await _lineService.RemoveRosterByEvent(
                eventId,
                _currentUser.UserId.Value,
                cancellationToken);

            return CreatedAtAction(nameof(RemoveRosterByEvent), new { id = result }, result);
        }

    }
}
