namespace HockeyPlanner.Backend.WebAPI.Models.Users
{
    public class UpdateUserRequest
    {
        public string FirstName { get; set; } = string.Empty;
        public string LastName { get; set; } = string.Empty;
        public int? JerseyNumber { get; set; }
        public int? PrimaryPosition { get; set; }
        public int? Handedness { get; set; }
        public int? Height { get; set; }
        public int? Weight { get; set; }
        public DateTime? BirthDate { get; set; }
        public string? PhotoUrl { get; set; }
        public Guid? SpbhlPlayerId { get; set; }
    }
}
