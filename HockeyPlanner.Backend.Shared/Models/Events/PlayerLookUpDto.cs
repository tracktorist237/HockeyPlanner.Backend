using HockeyPlanner.Backend.Core.Enums;

namespace HockeyPlanner.Backend.Shared.Models.Events
{
    public class PlayerLookUpDto
    {
        public Guid UserId { get; set; }
        public int? JerseyNumber { get; set; }
        public string FirstName { get; set; } = string.Empty;
        public string LastName { get; set; } = string.Empty;
        public PlayerRole Role { get; set; }
    }
}
