using HockeyPlanner.Backend.Core.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace HockeyPlanner.Backend.Infrastructure.Data.Configurations
{
    public class AttendanceConfiguration : IEntityTypeConfiguration<Attendance>
    {
        public void Configure(EntityTypeBuilder<Attendance> builder)
        {
            builder.HasKey(a => a.Id);

            builder.Property(a => a.Status)
                .HasConversion<int>()
                .IsRequired();

            builder.Property(a => a.RespondedAt)
                .IsRequired();

            builder.Property(a => a.Notes)
                .HasMaxLength(500);

            // Связи
            builder.HasOne(a => a.Event)
                .WithMany(e => e.Attendances)
                .HasForeignKey(a => a.EventId)
                .OnDelete(DeleteBehavior.Cascade);

            builder.HasOne(a => a.User)
                .WithMany()
                .HasForeignKey(a => a.UserId)
                .OnDelete(DeleteBehavior.Restrict);

            // Уникальный индекс (игрок не может быть дважды в одном мероприятии)
            builder.HasIndex(a => new { a.EventId, a.UserId })
                .IsUnique();

            // Индексы для быстрого поиска
            builder.HasIndex(a => a.EventId);
            builder.HasIndex(a => a.UserId);
        }
    }
}
