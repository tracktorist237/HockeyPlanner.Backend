using HockeyPlanner.Backend.Core.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace HockeyPlanner.Backend.Infrastructure.Data.Configurations
{
    public class TeamNewsConfiguration : IEntityTypeConfiguration<TeamNews>
    {
        public void Configure(EntityTypeBuilder<TeamNews> builder)
        {
            builder.HasKey(value => value.Id);

            builder.Property(value => value.Title)
                .IsRequired()
                .HasMaxLength(120);

            builder.Property(value => value.Body)
                .IsRequired()
                .HasMaxLength(2000);

            builder.HasIndex(value => value.TeamId);
            builder.HasIndex(value => value.CreatedAt);

            builder.HasOne(value => value.Team)
                .WithMany(value => value.News)
                .HasForeignKey(value => value.TeamId)
                .OnDelete(DeleteBehavior.Cascade);

            builder.HasOne(value => value.AuthorUser)
                .WithMany()
                .HasForeignKey(value => value.AuthorUserId)
                .OnDelete(DeleteBehavior.Cascade);
        }
    }
}
