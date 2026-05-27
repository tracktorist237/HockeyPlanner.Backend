using HockeyPlanner.Backend.Core.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace HockeyPlanner.Backend.Infrastructure.Data.Configurations
{
    public class NotificationConfiguration : IEntityTypeConfiguration<Notification>
    {
        public void Configure(EntityTypeBuilder<Notification> builder)
        {
            builder.HasKey(notification => notification.Id);

            builder.Property(notification => notification.Type)
                .HasConversion<int>()
                .IsRequired();

            builder.Property(notification => notification.Category)
                .HasConversion<int>()
                .IsRequired();

            builder.Property(notification => notification.Title)
                .IsRequired()
                .HasMaxLength(160);

            builder.Property(notification => notification.Body)
                .IsRequired()
                .HasMaxLength(1000);

            builder.Property(notification => notification.Url)
                .HasMaxLength(500);

            builder.HasOne(notification => notification.User)
                .WithMany()
                .HasForeignKey(notification => notification.UserId)
                .OnDelete(DeleteBehavior.Cascade);

            builder.HasIndex(notification => new { notification.UserId, notification.IsRead, notification.CreatedAt });
            builder.HasIndex(notification => notification.UserId);
        }
    }
}
