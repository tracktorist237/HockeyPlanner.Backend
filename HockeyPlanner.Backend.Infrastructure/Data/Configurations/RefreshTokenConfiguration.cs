using HockeyPlanner.Backend.Core.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace HockeyPlanner.Backend.Infrastructure.Data.Configurations
{
    public class RefreshTokenConfiguration : IEntityTypeConfiguration<RefreshToken>
    {
        public void Configure(EntityTypeBuilder<RefreshToken> builder)
        {
            builder.HasKey(value => value.Id);

            builder.Property(value => value.TokenHash)
                .IsRequired()
                .HasMaxLength(128);

            builder.Property(value => value.UserAgent)
                .HasMaxLength(500);

            builder.Property(value => value.IpAddress)
                .HasMaxLength(64);

            builder.HasIndex(value => value.TokenHash)
                .IsUnique();

            builder.HasIndex(value => value.UserId);

            builder.HasOne(value => value.User)
                .WithMany()
                .HasForeignKey(value => value.UserId)
                .OnDelete(DeleteBehavior.Cascade);
        }
    }
}
