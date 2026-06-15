using HockeyPlanner.Backend.Core.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace HockeyPlanner.Backend.Infrastructure.Data.Configurations
{
    public class TeamTableConfiguration : IEntityTypeConfiguration<TeamTable>
    {
        public void Configure(EntityTypeBuilder<TeamTable> builder)
        {
            builder.HasKey(value => value.Id);

            builder.Property(value => value.Name)
                .IsRequired()
                .HasMaxLength(120);

            builder.Property(value => value.TemplateType)
                .IsRequired();

            builder.HasIndex(value => value.TeamId);
            builder.HasIndex(value => new { value.TeamId, value.Name });
            builder.HasIndex(value => value.CreatedAt);

            builder.HasOne(value => value.Team)
                .WithMany(value => value.Tables)
                .HasForeignKey(value => value.TeamId)
                .OnDelete(DeleteBehavior.Cascade);

            builder.HasOne(value => value.CreatedByUser)
                .WithMany()
                .HasForeignKey(value => value.CreatedByUserId)
                .OnDelete(DeleteBehavior.Restrict);
        }
    }
}
