using HockeyPlanner.Backend.Shared.Models.Tables;

namespace HockeyPlanner.Backend.Application.Abstractions.Services;

public interface ITeamTablesService
{
    Task<IReadOnlyCollection<TeamTableSummaryDto>> GetTeamTables(Guid teamId, Guid actorUserId, CancellationToken cancellationToken);
    Task<IReadOnlyCollection<TeamTableSummaryDto>> GetTablesFeed(Guid actorUserId, CancellationToken cancellationToken);
    Task<TeamTableDto> GetTeamTable(Guid teamId, Guid tableId, Guid actorUserId, CancellationToken cancellationToken);
    Task<TeamTableDto> CreateTeamTable(Guid teamId, Guid actorUserId, CreateTeamTableRequest request, CancellationToken cancellationToken);
    Task<IReadOnlyCollection<EventTableProtocolDto>> GetEventProtocols(Guid eventId, Guid actorUserId, CancellationToken cancellationToken);
    Task<EventTableProtocolDto> CreateEventProtocol(Guid eventId, Guid actorUserId, CreateEventTableProtocolRequest request, CancellationToken cancellationToken);
    Task<EventTableProtocolDto> UpdateProtocol(Guid eventId, Guid protocolId, Guid actorUserId, UpdateEventTableProtocolRequest request, CancellationToken cancellationToken);
    Task<EventTableProtocolDto> UpdateProtocolRow(Guid eventId, Guid protocolId, Guid rowId, Guid actorUserId, UpdateEventTableProtocolRowRequest request, CancellationToken cancellationToken);
}
