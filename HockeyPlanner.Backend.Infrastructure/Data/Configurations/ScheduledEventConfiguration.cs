using HockeyPlanner.Backend.Core.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace HockeyPlanner.Backend.Infrastructure.Data.Configurations
{
    public class ScheduledEventConfiguration : IEntityTypeConfiguration<ScheduledEvent>
    {
        public void Configure(EntityTypeBuilder<ScheduledEvent> builder)
        {
            builder.HasKey(e => e.Id);

            builder.Property(e => e.Title)
                .IsRequired()
                .HasMaxLength(200);

            builder.Property(e => e.Description)
                .HasMaxLength(1000);

            builder.Property(e => e.Type)
                .HasConversion<int>()
                .IsRequired();

            builder.Property(e => e.Status)
                .HasConversion<int>()
                .IsRequired();

            builder.Property(e => e.StartTime)
                .IsRequired();

            builder.Property(e => e.LocationName)
                .IsRequired()
                .HasMaxLength(200);

            builder.Property(e => e.LocationAddress)
                .IsRequired()
                .HasMaxLength(300);

            builder.Property(e => e.IceRinkNumber)
                .HasMaxLength(50);

            // Связи
            builder.HasMany(e => e.Roster)
                .WithOne(l => l.Event)
                .HasForeignKey(l => l.EventId)
                .OnDelete(DeleteBehavior.Cascade);

            builder.HasMany(e => e.Attendances)
                .WithOne(a => a.Event)
                .HasForeignKey(a => a.EventId)
                .OnDelete(DeleteBehavior.Cascade);

            builder.HasOne(e => e.UniformColor)
                .WithMany(c => c.Events)
                .HasForeignKey(e => e.UniformColorId)
                .OnDelete(DeleteBehavior.SetNull);

            builder.HasOne(e => e.Team)
                .WithMany(t => t.Events)
                .HasForeignKey(e => e.TeamId)
                .OnDelete(DeleteBehavior.SetNull);

            // Индексы
            builder.HasIndex(e => e.StartTime);
            builder.HasIndex(e => e.TeamId);
        }
    }
}
