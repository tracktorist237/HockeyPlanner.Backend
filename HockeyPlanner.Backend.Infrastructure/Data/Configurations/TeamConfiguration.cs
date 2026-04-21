using HockeyPlanner.Backend.Core.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace HockeyPlanner.Backend.Infrastructure.Data.Configurations
{
    public class TeamConfiguration : IEntityTypeConfiguration<Team>
    {
        public void Configure(EntityTypeBuilder<Team> builder)
        {
            builder.HasKey(value => value.Id);

            builder.Property(value => value.Name)
                .IsRequired()
                .HasMaxLength(200);

            builder.Property(value => value.Description)
                .HasMaxLength(1000);

            builder.Property(value => value.Visibility)
                .IsRequired();

            builder.Property(value => value.InviteCode)
                .IsRequired()
                .HasMaxLength(20);

            builder.HasIndex(value => value.Name);
            builder.HasIndex(value => value.Visibility);
            builder.HasIndex(value => value.InviteCode).IsUnique();
            builder.HasIndex(value => new { value.Name, value.Visibility });

            builder.HasMany(value => value.Memberships)
                .WithOne(value => value.Team)
                .HasForeignKey(value => value.TeamId)
                .OnDelete(DeleteBehavior.Cascade);
        }
    }
}
