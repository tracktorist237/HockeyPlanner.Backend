using HockeyPlanner.Backend.Core.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace HockeyPlanner.Backend.Infrastructure.Data.Configurations
{
    public class PushSubscriptionConfiguration : IEntityTypeConfiguration<PushSubscription>
    {
        public void Configure(EntityTypeBuilder<PushSubscription> builder)
        {
            builder.HasKey(subscription => subscription.Id);

            builder.Property(subscription => subscription.Endpoint)
                .IsRequired()
                .HasMaxLength(2000);

            builder.Property(subscription => subscription.P256dhKey)
                .IsRequired()
                .HasMaxLength(500);

            builder.Property(subscription => subscription.AuthKey)
                .IsRequired()
                .HasMaxLength(500);

            builder.Property(subscription => subscription.UserAgent)
                .HasMaxLength(1000);

            builder.HasIndex(subscription => subscription.Endpoint)
                .IsUnique();
        }
    }
}

