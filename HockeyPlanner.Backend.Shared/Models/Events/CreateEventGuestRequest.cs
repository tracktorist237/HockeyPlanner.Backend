using HockeyPlanner.Backend.Core.Enums;

namespace HockeyPlanner.Backend.Shared.Models.Events
{
    public class CreateEventGuestRequest
    {
        public string FirstName { get; set; } = string.Empty;
        public string LastName { get; set; } = string.Empty;
        public Handedness? Handedness { get; set; }
        public int? JerseyNumber { get; set; }
    }
}
