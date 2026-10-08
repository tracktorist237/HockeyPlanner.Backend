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
                .HasForeignKey(p => new { p.LineId, p.EventId })
                .HasPrincipalKey(l => new { l.Id, l.EventId })
                .OnDelete(DeleteBehavior.Cascade); // Или Cascade

            // Связь с User
            builder.HasOne(p => p.User)
                .WithMany()
                .HasForeignKey(p => p.UserId)
                .OnDelete(DeleteBehavior.Restrict);

            builder.HasOne(p => p.EventGuest)
                .WithMany()
                .HasForeignKey(p => new { p.EventGuestId, p.EventId })
                .HasPrincipalKey(g => new { g.Id, g.EventId })
                .OnDelete(DeleteBehavior.Restrict);

            // PostgreSQL arbitrates membership across all lines, including concurrent writers.
            builder.HasIndex(p => new { p.EventId, p.UserId })
                .HasDatabaseName(RosterConstraints.UserIndex)
                .IsUnique()
                .HasFilter("user_id IS NOT NULL");

            builder.HasIndex(p => new { p.EventId, p.EventGuestId })
                .HasDatabaseName(RosterConstraints.GuestIndex)
                .IsUnique()
                .HasFilter("event_guest_id IS NOT NULL");

            // Индексы для поиска
            builder.HasIndex(p => p.LineId);
            builder.HasIndex(p => p.UserId);
            builder.HasIndex(p => p.EventGuestId);
            builder.HasIndex(p => p.Role);
        }
    }
}
