using HockeyPlanner.Backend.Core.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace HockeyPlanner.Backend.Infrastructure.Data.Configurations
{
    public class EventGuestConfiguration : IEntityTypeConfiguration<EventGuest>
    {
        public void Configure(EntityTypeBuilder<EventGuest> builder)
        {
            builder.HasKey(g => g.Id);

            builder.Property(g => g.FirstName)
                .HasMaxLength(100)
                .IsRequired();

            builder.Property(g => g.LastName)
                .HasMaxLength(100)
                .IsRequired();

            builder.Property(g => g.Handedness)
                .HasConversion<int?>();

            builder.Property(g => g.Status)
                .HasConversion<int>()
                .IsRequired();

            builder.Property(g => g.RespondedAt)
                .IsRequired();

            builder.Property(g => g.Notes)
                .HasMaxLength(500);

            builder.HasOne(g => g.Event)
                .WithMany(e => e.EventGuests)
                .HasForeignKey(g => g.EventId)
                .OnDelete(DeleteBehavior.Cascade);

            builder.HasOne(g => g.InvitedByUser)
                .WithMany()
                .HasForeignKey(g => g.InvitedByUserId)
                .OnDelete(DeleteBehavior.Restrict);

            builder.HasIndex(g => g.EventId);
            builder.HasIndex(g => g.InvitedByUserId);
        }
    }
}
