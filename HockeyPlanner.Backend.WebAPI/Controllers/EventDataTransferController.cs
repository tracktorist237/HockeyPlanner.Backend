using HockeyPlanner.Backend.Application.Abstractions.Identity;
using HockeyPlanner.Backend.Core.Exceptions;
using HockeyPlanner.Backend.WebAPI.Models.Events;
using HockeyPlanner.Backend.WebAPI.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace HockeyPlanner.Backend.WebAPI.Controllers;

[ApiController]
[Authorize]
[Route("api/events/{sourceEventId:guid}/transfer")]
public sealed class EventDataTransferController(IEventDataTransferService service, ICurrentUser currentUser) : ControllerBase
{
    [HttpPost("preview")]
    public async Task<ActionResult<AttendanceTransferPreviewDto>> PreviewAttendance(
        Guid sourceEventId,
        PreviewAttendanceTransferRequest request,
        CancellationToken cancellationToken)
    {
        if (!currentUser.UserId.HasValue) return Unauthorized();
        try
        {
            return Ok(await service.PreviewAttendanceAsync(sourceEventId, currentUser.UserId.Value, request, cancellationToken));
        }
        catch (NotFoundException exception) { return NotFound(new { error = exception.Message }); }
        catch (UnauthorizedException exception) { return StatusCode(403, new { error = exception.Message }); }
        catch (BusinessRuleException exception) { return BadRequest(new { error = exception.Message }); }
    }

    [HttpPost]
    public async Task<IActionResult> Transfer(Guid sourceEventId, TransferEventDataRequest request, CancellationToken cancellationToken)
    {
        if (!currentUser.UserId.HasValue) return Unauthorized();
        try
        {
            await service.TransferAsync(sourceEventId, currentUser.UserId.Value, request, cancellationToken);
            return Ok(new { targetEventId = request.TargetEventId });
        }
        catch (NotFoundException exception) { return NotFound(new { error = exception.Message }); }
        catch (UnauthorizedException exception) { return StatusCode(403, new { error = exception.Message }); }
        catch (BusinessRuleException exception) { return BadRequest(new { error = exception.Message }); }
    }
}
