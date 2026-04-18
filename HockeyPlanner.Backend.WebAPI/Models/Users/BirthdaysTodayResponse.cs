namespace HockeyPlanner.Backend.WebAPI.Models.Users
{
    public class BirthdayUserDto
    {
        public Guid UserId { get; set; }
        public string FirstName { get; set; } = string.Empty;
        public string LastName { get; set; } = string.Empty;
        public int? JerseyNumber { get; set; }
        public int? Age { get; set; }
    }

    public class BirthdaysTodayResponse
    {
        public string Date { get; set; } = string.Empty;
        public List<BirthdayUserDto> Users { get; set; } = new();
    }
}

