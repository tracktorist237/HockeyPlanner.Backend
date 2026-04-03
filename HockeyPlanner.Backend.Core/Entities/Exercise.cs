using HockeyPlanner.Backend.Core.Entities.Base;

namespace HockeyPlanner.Backend.Core.Entities
{
    public class Exercise : Entity
    {
        public string Name { get; set; } = string.Empty;
        public string VideoUrl { get; set; } = string.Empty;
        public Guid CreatedByUserId { get; set; }

        public List<ScheduledEventExercise> ScheduledEventExercises { get; set; } = new();
    }
}

