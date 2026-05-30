using HockeyPlanner.Backend.Core.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace HockeyPlanner.Backend.Infrastructure.Data.Configurations
{
    public class ReleaseNoticeConfiguration : IEntityTypeConfiguration<ReleaseNotice>
    {
        public void Configure(EntityTypeBuilder<ReleaseNotice> builder)
        {
            builder.HasKey(release => release.Id);

            builder.Property(release => release.Version)
                .IsRequired()
                .HasMaxLength(50);

            builder.Property(release => release.Title)
                .IsRequired()
                .HasMaxLength(180);

            builder.Property(release => release.Body)
                .IsRequired()
                .HasMaxLength(4000);

            builder.Property(release => release.IsPublished)
                .IsRequired();

            builder.Property(release => release.SendNotification)
                .IsRequired();

            builder.Property(release => release.NotificationSent)
                .IsRequired();

            builder.HasOne(release => release.CreatedByUser)
                .WithMany()
                .HasForeignKey(release => release.CreatedByUserId)
                .OnDelete(DeleteBehavior.SetNull);

            builder.HasIndex(release => release.IsPublished);
            builder.HasIndex(release => release.PublishedAt);
            builder.HasIndex(release => release.Version);
        }
    }
}
