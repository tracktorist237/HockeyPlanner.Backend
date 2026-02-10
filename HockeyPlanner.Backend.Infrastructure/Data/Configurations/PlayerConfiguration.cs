using HockeyPlanner.Backend.Core.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using System;
using System.Collections.Generic;
using System.Text;

namespace HockeyPlanner.Backend.Infrastructure.Data.Configurations
{
    public class PlayerConfiguration : IEntityTypeConfiguration<Player>
    {
        public void Configure(EntityTypeBuilder<Player> builder)
        {
            builder.HasKey(p => p.Id);

            builder.Property(p => p.Role)
                .HasConversion<int>()
                .IsRequired();

            // Связь с Line
            builder.HasOne(p => p.Line)
                .WithMany(l => l.Players)
                .HasForeignKey(p => p.LineId)
                .OnDelete(DeleteBehavior.Cascade); // Или Cascade

            // Связь с User
            builder.HasOne(p => p.User)
                .WithMany()
                .HasForeignKey(p => p.UserId)
                .OnDelete(DeleteBehavior.Restrict);

            // Уникальный индекс (игрок не может быть в одной линии дважды)
            builder.HasIndex(p => new { p.LineId, p.UserId }) // ✅ Исправлено с Id на LineId
                .IsUnique();

            // Индексы для поиска
            builder.HasIndex(p => p.LineId);
            builder.HasIndex(p => p.UserId);
            builder.HasIndex(p => p.Role);
        }
    }
}
