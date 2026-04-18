using HockeyPlanner.Backend.Core.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace HockeyPlanner.Backend.Infrastructure.Data.Configurations
{
    public class UserConfiguration : IEntityTypeConfiguration<User>
    {
        public void Configure(EntityTypeBuilder<User> builder)
        {
            builder.HasKey(u => u.Id);

            builder.Property(u => u.FirstName)
                .IsRequired()
                .HasMaxLength(100);

            builder.Property(u => u.LastName)
                .IsRequired()
                .HasMaxLength(100);

            builder.Property(u => u.Email)
                .HasMaxLength(200);

            builder.Property(u => u.Phone)
                .HasMaxLength(20);

            builder.Property(u => u.PhotoUrl)
                .HasMaxLength(500);

            builder.Property(u => u.SpbhlPlayerId);

            builder.Property(u => u.Role)
                .HasConversion<int>()
                .IsRequired();

            builder.Property(u => u.JerseyNumber);

            builder.Property(u => u.PrimaryPosition)
                .HasConversion<int>();

            // Индексы для поиска
            builder.HasIndex(u => u.LastName);
            builder.HasIndex(u => u.Role);
            builder.HasIndex(u => u.JerseyNumber);
            builder.HasIndex(u => u.SpbhlPlayerId);

            // Для быстрого поиска по имени
            builder.HasIndex(u => new { u.LastName, u.FirstName });
        }
    }
}
