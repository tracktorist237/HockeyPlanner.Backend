namespace HockeyPlanner.Backend.Core.Entities
{
    public class ScheduledEventExercise
    {
        public Guid ScheduledEventId { get; set; }
        public Guid ExerciseId { get; set; }
        public int Order { get; set; }

        public ScheduledEvent ScheduledEvent { get; set; } = null!;
        public Exercise Exercise { get; set; } = null!;
    }
}

