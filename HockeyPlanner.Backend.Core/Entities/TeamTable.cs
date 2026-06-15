using HockeyPlanner.Backend.Core.Entities.Base;
using HockeyPlanner.Backend.Core.Enums;

namespace HockeyPlanner.Backend.Core.Entities
{
    public class TeamTable : Entity
    {
        public Guid TeamId { get; set; }
        public Team Team { get; set; } = null!;

        public string Name { get; set; } = string.Empty;
        public TeamTableTemplateType TemplateType { get; set; }
        public Guid CreatedByUserId { get; set; }
        public User CreatedByUser { get; set; } = null!;

        public ICollection<TeamTableRow> Rows { get; set; } = new List<TeamTableRow>();
        public ICollection<EventTableProtocol> Protocols { get; set; } = new List<EventTableProtocol>();
    }
}
