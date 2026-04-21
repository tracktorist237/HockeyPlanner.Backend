using HockeyPlanner.Backend.Core.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace HockeyPlanner.Backend.Infrastructure.Data.Configurations
{
    public class TeamMembershipConfiguration : IEntityTypeConfiguration<TeamMembership>
    {
        public void Configure(EntityTypeBuilder<TeamMembership> builder)
        {
            builder.HasKey(value => value.Id);

            builder.Property(value => value.Role)
                .IsRequired();

            builder.HasIndex(value => value.TeamId);
            builder.HasIndex(value => value.UserId);
            builder.HasIndex(value => new { value.TeamId, value.UserId }).IsUnique();

            builder.HasOne(value => value.Team)
                .WithMany(value => value.Memberships)
                .HasForeignKey(value => value.TeamId)
                .OnDelete(DeleteBehavior.Cascade);

            builder.HasOne(value => value.User)
                .WithMany()
                .HasForeignKey(value => value.UserId)
                .OnDelete(DeleteBehavior.Cascade);
        }
    }
}
