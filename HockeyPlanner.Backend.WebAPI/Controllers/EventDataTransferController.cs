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
        return Ok(await service.PreviewAttendanceAsync(sourceEventId, currentUser.UserId.Value, request, cancellationToken));
    }

    [HttpPost]
    public async Task<IActionResult> Transfer(Guid sourceEventId, TransferEventDataRequest request, CancellationToken cancellationToken)
    {
        if (!currentUser.UserId.HasValue) return Unauthorized();
        await service.TransferAsync(sourceEventId, currentUser.UserId.Value, request, cancellationToken);
        return Ok(new { targetEventId = request.TargetEventId });
    }
}
