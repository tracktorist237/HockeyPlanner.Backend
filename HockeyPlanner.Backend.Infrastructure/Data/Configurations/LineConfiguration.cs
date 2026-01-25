using HockeyPlanner.Backend.Core.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace HockeyPlanner.Backend.Infrastructure.Data.Configurations
{
    public class LineConfiguration : IEntityTypeConfiguration<Line>
    {
        public void Configure(EntityTypeBuilder<Line> builder)
        {
            builder.HasKey(l => l.Id);

            builder.Property(l => l.Name)
                .IsRequired()
                .HasMaxLength(100);

            builder.Property(l => l.Order)
                .IsRequired()
                .HasDefaultValue(1);

            // Связи
            builder.HasOne(l => l.Event)
                .WithMany(r => r.Roster)
                .HasForeignKey(l => l.EventId)
                .OnDelete(DeleteBehavior.SetNull);

            builder.HasMany(l => l.Players)
                .WithOne()
                .HasForeignKey(lm => lm.LineId)
                .OnDelete(DeleteBehavior.Cascade);

            // Индекс для сортировки
            builder.HasIndex(l => new { l.EventId, l.Order });
        }
    }
}
