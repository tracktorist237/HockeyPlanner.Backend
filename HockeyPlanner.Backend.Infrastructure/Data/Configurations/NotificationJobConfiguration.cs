using HockeyPlanner.Backend.Core.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace HockeyPlanner.Backend.Infrastructure.Data.Configurations;

public sealed class NotificationJobConfiguration : IEntityTypeConfiguration<NotificationJob>
{
    public void Configure(EntityTypeBuilder<NotificationJob> builder)
    {
        builder.HasKey(value => value.Id);
        builder.Property(value => value.Status).HasConversion<int>();
        builder.Property(value => value.LastErrorCode).HasMaxLength(100);
        builder.HasOne(value => value.Notification).WithMany()
            .HasForeignKey(value => value.NotificationId).OnDelete(DeleteBehavior.Cascade);
        builder.HasIndex(value => value.NotificationId).IsUnique().HasDatabaseName("ux_notification_jobs_notification");
        builder.HasIndex(value => new { value.Status, value.NextAttemptAt }).HasDatabaseName("ix_notification_jobs_due");
        builder.HasIndex(value => new { value.Status, value.ClaimedAt }).HasDatabaseName("ix_notification_jobs_recovery");
    }
}
