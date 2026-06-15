using HockeyPlanner.Backend.Core.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace HockeyPlanner.Backend.Infrastructure.Data.Configurations
{
    public class TeamTableRowConfiguration : IEntityTypeConfiguration<TeamTableRow>
    {
        public void Configure(EntityTypeBuilder<TeamTableRow> builder)
        {
            builder.HasKey(value => value.Id);

            builder.HasIndex(value => value.TeamTableId);
            builder.HasIndex(value => new { value.TeamTableId, value.UserId }).IsUnique();

            builder.HasOne(value => value.TeamTable)
                .WithMany(value => value.Rows)
                .HasForeignKey(value => value.TeamTableId)
                .OnDelete(DeleteBehavior.Cascade);

            builder.HasOne(value => value.User)
                .WithMany()
                .HasForeignKey(value => value.UserId)
                .OnDelete(DeleteBehavior.Cascade);
        }
    }
}
