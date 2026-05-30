using HockeyPlanner.Backend.Core.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace HockeyPlanner.Backend.Infrastructure.Data.Configurations
{
    public class NotificationDeliveryConfiguration : IEntityTypeConfiguration<NotificationDelivery>
    {
        public void Configure(EntityTypeBuilder<NotificationDelivery> builder)
        {
            builder.HasKey(delivery => delivery.Id);

            builder.Property(delivery => delivery.Status)
                .HasConversion<int>()
                .IsRequired();

            builder.Property(delivery => delivery.Error)
                .HasMaxLength(1000);

            builder.Property(delivery => delivery.EndpointHash)
                .HasMaxLength(128);

            builder.HasOne(delivery => delivery.Notification)
                .WithMany()
                .HasForeignKey(delivery => delivery.NotificationId)
                .OnDelete(DeleteBehavior.Cascade);

            builder.HasOne(delivery => delivery.User)
                .WithMany()
                .HasForeignKey(delivery => delivery.UserId)
                .OnDelete(DeleteBehavior.Cascade);

            builder.HasOne(delivery => delivery.PushSubscription)
                .WithMany()
                .HasForeignKey(delivery => delivery.PushSubscriptionId)
                .OnDelete(DeleteBehavior.SetNull);

            builder.HasIndex(delivery => delivery.Status);
            builder.HasIndex(delivery => delivery.UserId);
            builder.HasIndex(delivery => delivery.NotificationId);
            builder.HasIndex(delivery => delivery.CreatedAt);
        }
    }
}
