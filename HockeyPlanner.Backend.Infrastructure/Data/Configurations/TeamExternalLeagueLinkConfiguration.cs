using HockeyPlanner.Backend.Core.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace HockeyPlanner.Backend.Infrastructure.Data.Configurations
{
    public class TeamExternalLeagueLinkConfiguration : IEntityTypeConfiguration<TeamExternalLeagueLink>
    {
        public void Configure(EntityTypeBuilder<TeamExternalLeagueLink> builder)
        {
            builder.HasKey(value => value.Id);

            builder.Property(value => value.Provider)
                .IsRequired();

            builder.Property(value => value.ExternalTeamId)
                .IsRequired()
                .HasMaxLength(200);

            builder.Property(value => value.ExternalTeamName)
                .IsRequired()
                .HasMaxLength(200);

            builder.Property(value => value.DivisionName)
                .HasMaxLength(200);

            builder.Property(value => value.ProfileUrl)
                .HasMaxLength(500);

            builder.Property(value => value.LogoUrl)
                .HasMaxLength(1000);

            builder.Property(value => value.CoverUrl)
                .HasMaxLength(1000);

            builder.Property(value => value.City)
                .HasMaxLength(200);

            builder.Property(value => value.Country)
                .HasMaxLength(100);

            builder.Property(value => value.CoachName)
                .HasMaxLength(200);

            builder.Property(value => value.AdministratorName)
                .HasMaxLength(200);

            builder.Property(value => value.PhonesJson)
                .HasColumnType("text");

            builder.Property(value => value.WebsiteUrlsJson)
                .HasColumnType("text");

            builder.Property(value => value.IsPrimary)
                .IsRequired();

            builder.HasIndex(value => value.TeamId);
            builder.HasIndex(value => new { value.Provider, value.ExternalTeamId })
                .IsUnique();

            builder.HasOne(value => value.Team)
                .WithMany(value => value.ExternalLeagueLinks)
                .HasForeignKey(value => value.TeamId)
                .OnDelete(DeleteBehavior.Cascade);
        }
    }
}
