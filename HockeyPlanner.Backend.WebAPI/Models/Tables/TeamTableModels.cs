using HockeyPlanner.Backend.Core.Enums;

namespace HockeyPlanner.Backend.WebAPI.Models.Tables
{
    public class TeamTableDto
    {
        public Guid Id { get; set; }
        public Guid TeamId { get; set; }
        public string TeamName { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public TeamTableTemplateType TemplateType { get; set; }
        public bool CanManage { get; set; }
        public DateTime CreatedAt { get; set; }
        public IReadOnlyCollection<TeamTableRowDto> Rows { get; set; } = Array.Empty<TeamTableRowDto>();
    }

    public class TeamTableSummaryDto
    {
        public Guid Id { get; set; }
        public Guid TeamId { get; set; }
        public string TeamName { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public TeamTableTemplateType TemplateType { get; set; }
        public bool CanManage { get; set; }
        public DateTime CreatedAt { get; set; }
        public int RowsCount { get; set; }
    }

    public class TeamTableRowDto
    {
        public Guid Id { get; set; }
        public Guid UserId { get; set; }
        public string PlayerName { get; set; } = string.Empty;
        public int? JerseyNumber { get; set; }
        public string? PhotoUrl { get; set; }
        public int Games { get; set; }
        public int Goals { get; set; }
        public int Assists { get; set; }
        public int Points { get; set; }
    }

    public class CreateTeamTableRequest
    {
        public string Name { get; set; } = string.Empty;
        public TeamTableTemplateType TemplateType { get; set; } = TeamTableTemplateType.PlayerStats;
    }

    public class EventTableProtocolDto
    {
        public Guid Id { get; set; }
        public Guid EventId { get; set; }
        public string EventTitle { get; set; } = string.Empty;
        public Guid TeamTableId { get; set; }
        public string TeamTableName { get; set; } = string.Empty;
        public bool CanManage { get; set; }
        public DateTime CreatedAt { get; set; }
        public IReadOnlyCollection<EventTableProtocolRowDto> Rows { get; set; } = Array.Empty<EventTableProtocolRowDto>();
    }

    public class EventTableProtocolRowDto
    {
        public Guid Id { get; set; }
        public Guid UserId { get; set; }
        public string PlayerName { get; set; } = string.Empty;
        public int? JerseyNumber { get; set; }
        public string? PhotoUrl { get; set; }
        public int Games { get; set; }
        public int Goals { get; set; }
        public int Assists { get; set; }
        public int Points { get; set; }
    }

    public class CreateEventTableProtocolRequest
    {
        public Guid TeamTableId { get; set; }
    }

    public class UpdateEventTableProtocolRowRequest
    {
        public int Games { get; set; }
        public int Goals { get; set; }
        public int Assists { get; set; }
    }

    public class UpdateEventTableProtocolRequest
    {
        public List<UpdateEventTableProtocolRowDto> Rows { get; set; } = new();
    }

    public class UpdateEventTableProtocolRowDto
    {
        public Guid RowId { get; set; }
        public int Games { get; set; }
        public int Goals { get; set; }
        public int Assists { get; set; }
    }
}
