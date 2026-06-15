using HockeyPlanner.Backend.Core.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace HockeyPlanner.Backend.Infrastructure.Data.Configurations
{
    public class EventTableProtocolRowConfiguration : IEntityTypeConfiguration<EventTableProtocolRow>
    {
        public void Configure(EntityTypeBuilder<EventTableProtocolRow> builder)
        {
            builder.HasKey(value => value.Id);

            builder.HasIndex(value => value.EventTableProtocolId);
            builder.HasIndex(value => new { value.EventTableProtocolId, value.UserId }).IsUnique();

            builder.HasOne(value => value.EventTableProtocol)
                .WithMany(value => value.Rows)
                .HasForeignKey(value => value.EventTableProtocolId)
                .OnDelete(DeleteBehavior.Cascade);

            builder.HasOne(value => value.User)
                .WithMany()
                .HasForeignKey(value => value.UserId)
                .OnDelete(DeleteBehavior.Cascade);
        }
    }
}
