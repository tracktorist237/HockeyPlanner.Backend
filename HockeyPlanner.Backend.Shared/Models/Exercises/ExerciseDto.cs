namespace HockeyPlanner.Backend.Shared.Models.Exercises
{
    public class ExerciseDto
    {
        public Guid Id { get; set; }
        public string Name { get; set; } = string.Empty;
        public string VideoUrl { get; set; } = string.Empty;
    }
}

