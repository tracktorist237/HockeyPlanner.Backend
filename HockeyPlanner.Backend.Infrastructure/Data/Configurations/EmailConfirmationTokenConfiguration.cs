using HockeyPlanner.Backend.Core.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace HockeyPlanner.Backend.Infrastructure.Data.Configurations
{
    public class EmailConfirmationTokenConfiguration : IEntityTypeConfiguration<EmailConfirmationToken>
    {
        public void Configure(EntityTypeBuilder<EmailConfirmationToken> builder)
        {
            builder.HasKey(value => value.Id);

            builder.Property(value => value.TokenHash)
                .IsRequired()
                .HasMaxLength(128);

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
