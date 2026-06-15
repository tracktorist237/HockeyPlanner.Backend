using HockeyPlanner.Backend.Core.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace HockeyPlanner.Backend.Infrastructure.Data.Configurations
{
    public class EventTableProtocolConfiguration : IEntityTypeConfiguration<EventTableProtocol>
    {
        public void Configure(EntityTypeBuilder<EventTableProtocol> builder)
        {
            builder.HasKey(value => value.Id);

            builder.HasIndex(value => value.EventId);
            builder.HasIndex(value => value.TeamTableId);
            builder.HasIndex(value => new { value.EventId, value.TeamTableId }).IsUnique();

            builder.HasOne(value => value.Event)
                .WithMany(value => value.TableProtocols)
                .HasForeignKey(value => value.EventId)
                .OnDelete(DeleteBehavior.Cascade);

            builder.HasOne(value => value.TeamTable)
                .WithMany(value => value.Protocols)
                .HasForeignKey(value => value.TeamTableId)
                .OnDelete(DeleteBehavior.Cascade);

            builder.HasOne(value => value.CreatedByUser)
                .WithMany()
                .HasForeignKey(value => value.CreatedByUserId)
                .OnDelete(DeleteBehavior.Restrict);
        }
    }
}
