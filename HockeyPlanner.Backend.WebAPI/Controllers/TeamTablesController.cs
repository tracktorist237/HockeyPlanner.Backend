using HockeyPlanner.Backend.Application.Abstractions.Identity;
using HockeyPlanner.Backend.Application.Abstractions.Services;
using HockeyPlanner.Backend.Core.Exceptions;
using HockeyPlanner.Backend.Shared.Models.Tables;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace HockeyPlanner.Backend.WebAPI.Controllers;

[ApiController]
[Authorize]
public sealed class TeamTablesController(ITeamTablesService service, ICurrentUser currentUser) : ControllerBase
{
    private Guid ActorUserId => currentUser.UserId is Guid id && id != Guid.Empty
        ? id : throw new AuthenticationRequiredException("Требуется авторизация.");

    [HttpGet("api/teams/{teamId:guid}/tables")]
    public async Task<ActionResult<IReadOnlyCollection<TeamTableSummaryDto>>> GetTeamTables(Guid teamId, CancellationToken cancellationToken) =>
        Ok(await service.GetTeamTables(teamId, ActorUserId, cancellationToken));

    [HttpGet("api/news/tables")]
    public async Task<ActionResult<IReadOnlyCollection<TeamTableSummaryDto>>> GetTablesFeed(CancellationToken cancellationToken) =>
        Ok(await service.GetTablesFeed(ActorUserId, cancellationToken));

    [HttpGet("api/teams/{teamId:guid}/tables/{tableId:guid}")]
    public async Task<ActionResult<TeamTableDto>> GetTeamTable(Guid teamId, Guid tableId, CancellationToken cancellationToken) =>
        Ok(await service.GetTeamTable(teamId, tableId, ActorUserId, cancellationToken));

    [HttpPost("api/teams/{teamId:guid}/tables")]
    public async Task<ActionResult<TeamTableDto>> CreateTeamTable(Guid teamId, [FromBody] CreateTeamTableRequest request, CancellationToken cancellationToken) =>
        Ok(await service.CreateTeamTable(teamId, ActorUserId, request, cancellationToken));

    [HttpGet("api/events/{eventId:guid}/table-protocols")]
    public async Task<ActionResult<IReadOnlyCollection<EventTableProtocolDto>>> GetEventProtocols(Guid eventId, CancellationToken cancellationToken) =>
        Ok(await service.GetEventProtocols(eventId, ActorUserId, cancellationToken));

    [HttpPost("api/events/{eventId:guid}/table-protocols")]
    public async Task<ActionResult<EventTableProtocolDto>> CreateEventProtocol(Guid eventId, [FromBody] CreateEventTableProtocolRequest request, CancellationToken cancellationToken) =>
        Ok(await service.CreateEventProtocol(eventId, ActorUserId, request, cancellationToken));

    [HttpPut("api/events/{eventId:guid}/table-protocols/{protocolId:guid}")]
    public async Task<ActionResult<EventTableProtocolDto>> UpdateProtocol(Guid eventId, Guid protocolId, [FromBody] UpdateEventTableProtocolRequest request, CancellationToken cancellationToken) =>
        Ok(await service.UpdateProtocol(eventId, protocolId, ActorUserId, request, cancellationToken));

    [HttpPut("api/events/{eventId:guid}/table-protocols/{protocolId:guid}/rows/{rowId:guid}")]
    public async Task<ActionResult<EventTableProtocolDto>> UpdateProtocolRow(Guid eventId, Guid protocolId, Guid rowId, [FromBody] UpdateEventTableProtocolRowRequest request, CancellationToken cancellationToken) =>
        Ok(await service.UpdateProtocolRow(eventId, protocolId, rowId, ActorUserId, request, cancellationToken));

}
