using HockeyPlanner.Backend.Core.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace HockeyPlanner.Backend.Infrastructure.Data.Configurations
{
    public class NotificationPreferencesConfiguration : IEntityTypeConfiguration<NotificationPreferences>
    {
        public void Configure(EntityTypeBuilder<NotificationPreferences> builder)
        {
            builder.HasKey(preferences => preferences.Id);

            builder.HasOne(preferences => preferences.User)
                .WithMany()
                .HasForeignKey(preferences => preferences.UserId)
                .OnDelete(DeleteBehavior.Cascade);

            builder.HasIndex(preferences => preferences.UserId)
                .IsUnique();
        }
    }
}
