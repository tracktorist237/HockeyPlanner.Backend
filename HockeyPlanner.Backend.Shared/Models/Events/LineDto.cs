using HockeyPlanner.Backend.Shared.Models.UniformColors;

namespace HockeyPlanner.Backend.Shared.Models.Events
{
    public class LineDto
    {
        public Guid Id { get; set; }
        public string Name { get; set; } = string.Empty;
        public int Order { get; set; } = 1;
        public Guid? UniformColorId { get; set; }
        public UniformColorDto? UniformColor { get; set; }

        public List<PlayerLookUpDto> Members { get; set; } = new();
    }
}
