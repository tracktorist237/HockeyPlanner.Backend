using HockeyPlanner.Backend.Core.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace HockeyPlanner.Backend.Infrastructure.Data.Configurations
{
    public class ScheduledEventExerciseConfiguration : IEntityTypeConfiguration<ScheduledEventExercise>
    {
        public void Configure(EntityTypeBuilder<ScheduledEventExercise> builder)
        {
            builder.HasKey(x => new { x.ScheduledEventId, x.ExerciseId });

            builder.Property(x => x.Order)
                .IsRequired();

            builder.HasOne(x => x.ScheduledEvent)
                .WithMany(x => x.ScheduledEventExercises)
                .HasForeignKey(x => x.ScheduledEventId)
                .OnDelete(DeleteBehavior.Cascade);

            builder.HasOne(x => x.Exercise)
                .WithMany(x => x.ScheduledEventExercises)
                .HasForeignKey(x => x.ExerciseId)
                .OnDelete(DeleteBehavior.Restrict);

            builder.HasIndex(x => new { x.ScheduledEventId, x.Order });
        }
    }
}

