using HockeyPlanner.Backend.Core.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace HockeyPlanner.Backend.Infrastructure.Data.Configurations
{
    public class UserPrivacySettingsConfiguration : IEntityTypeConfiguration<UserPrivacySettings>
    {
        public void Configure(EntityTypeBuilder<UserPrivacySettings> builder)
        {
            builder.HasKey(value => value.Id);

            builder.HasIndex(value => value.UserId)
                .IsUnique();

            builder.HasOne(value => value.User)
                .WithOne()
                .HasForeignKey<UserPrivacySettings>(value => value.UserId)
                .OnDelete(DeleteBehavior.Cascade);

            builder.Property(value => value.EmailVisibility)
                .HasConversion<int>()
                .IsRequired();

            builder.Property(value => value.PhoneVisibility)
                .HasConversion<int>()
                .IsRequired();

            builder.Property(value => value.BirthDateVisibility)
                .HasConversion<int>()
                .IsRequired();

            builder.Property(value => value.PhysicalVisibility)
                .HasConversion<int>()
                .IsRequired();

            builder.Property(value => value.HockeyProfileVisibility)
                .HasConversion<int>()
                .IsRequired();

            builder.Property(value => value.SpbhlProfileVisibility)
                .HasConversion<int>()
                .IsRequired();
        }
    }
}
