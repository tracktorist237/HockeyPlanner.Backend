using HockeyPlanner.Backend.Core.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace HockeyPlanner.Backend.Infrastructure.Data.Configurations
{
    public class AppReportConfiguration : IEntityTypeConfiguration<AppReport>
    {
        public void Configure(EntityTypeBuilder<AppReport> builder)
        {
            builder.HasKey(report => report.Id);

            builder.Property(report => report.Type)
                .HasConversion<int>()
                .IsRequired();

            builder.Property(report => report.Status)
                .HasConversion<int>()
                .IsRequired();

            builder.Property(report => report.Severity)
                .HasConversion<int>()
                .IsRequired();

            builder.Property(report => report.Title)
                .IsRequired()
                .HasMaxLength(180);

            builder.Property(report => report.Message)
                .IsRequired()
                .HasMaxLength(4000);

            builder.Property(report => report.Route)
                .HasMaxLength(500);

            builder.Property(report => report.EntityType)
                .HasMaxLength(100);

            builder.Property(report => report.AppVersion)
                .HasMaxLength(50);

            builder.Property(report => report.Platform)
                .HasMaxLength(100);

            builder.Property(report => report.UserAgent)
                .HasMaxLength(500);

            builder.HasOne(report => report.User)
                .WithMany()
                .HasForeignKey(report => report.UserId)
                .OnDelete(DeleteBehavior.SetNull);

            builder.HasIndex(report => report.Status);
            builder.HasIndex(report => report.Type);
            builder.HasIndex(report => report.Severity);
            builder.HasIndex(report => report.UserId);
            builder.HasIndex(report => report.CreatedAt);
        }
    }
}
