using HockeyPlanner.Backend.Application.Abstractions.Services;
using HockeyPlanner.Backend.Core.Entities;
using HockeyPlanner.Backend.Core.Exceptions;
using HockeyPlanner.Backend.Shared.Models.Lines;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace HockeyPlanner.Backend.WebAPI.Controllers
{
    [ApiController]
    public class LinesController : ControllerBase
    {
        private readonly ILineService _lineService;

        public LinesController(ILineService lineService)
        {
            _lineService = lineService;
        }

        [HttpGet]
        [Route("api/lines")]
        public async Task<IActionResult> GetRosterByEvent([FromQuery] Guid eventId)
        {
            try
            {
                var result = await _lineService.GetRosterByEvent(eventId);

                return CreatedAtAction(nameof(GetRosterByEvent), new { id = result }, result);
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
                return StatusCode(500, new { error = "Внутренняя ошибка сервера" });
            }
        }

        [HttpPost]
        [Route("api/lines")]
        public async Task<IActionResult> CreateRoster([FromBody] CreateUpdateRosterRequest request, [FromQuery] Guid currentUserId)
        {
            try
            {
                var result = await _lineService.CreateRoster(request, currentUserId);

                return CreatedAtAction(nameof(CreateRoster), new { id = result }, result);
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
                return StatusCode(500, new { error = "Внутренняя ошибка сервера" });
            }
        }

        [HttpPut]
        [Route("api/lines")]
        public async Task<IActionResult> UpdateRoster([FromBody] CreateUpdateRosterRequest request, Guid currentUserId)
        {
            try
            {
                var result = await _lineService.UpdateRoster(request, currentUserId);

                return CreatedAtAction(nameof(UpdateRoster), new { id = result }, result);
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
                return StatusCode(500, new { error = "Внутренняя ошибка сервера" });
            }
        }

        [HttpDelete]
        [Route("api/lines")]
        public async Task<IActionResult> RemoveRosterByEvent([FromQuery] Guid eventId, Guid currentUserId)
        {
            try
            {
                var result = await _lineService.RemoveRosterByEvent(eventId, currentUserId);

                return CreatedAtAction(nameof(RemoveRosterByEvent), new { id = result }, result);
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
                return StatusCode(500, new { error = "Внутренняя ошибка сервера" });
            }
        }
    }
}
