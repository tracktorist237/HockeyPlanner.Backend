using HockeyPlanner.Backend.Core.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace HockeyPlanner.Backend.Infrastructure.Data.Configurations;

public sealed class ExternalEventSuppressionConfiguration : IEntityTypeConfiguration<ExternalEventSuppression>
{
    public void Configure(EntityTypeBuilder<ExternalEventSuppression> builder)
    {
        builder.HasKey(value => value.Id);
        builder.Property(value => value.ExternalLeagueProvider).HasConversion<int>().IsRequired();
        builder.Property(value => value.ExternalCompetitionId).HasMaxLength(200).IsRequired();
        builder.Property(value => value.ExternalMatchId).HasMaxLength(200).IsRequired();
        builder.Property(value => value.Reason).HasMaxLength(500);
        builder.Property(value => value.ExternalTitle).HasMaxLength(200);
        builder.Property(value => value.CompetitionName).HasMaxLength(200);
        builder.HasIndex(value => new { value.TeamId, value.ExternalLeagueProvider, value.ExternalCompetitionId, value.ExternalMatchId })
            .IsUnique().HasDatabaseName("ix_external_event_suppressions_identity");
        builder.HasOne(value => value.Team).WithMany().HasForeignKey(value => value.TeamId).OnDelete(DeleteBehavior.Cascade);
        builder.HasOne(value => value.CreatedByUser).WithMany().HasForeignKey(value => value.CreatedByUserId).OnDelete(DeleteBehavior.SetNull);
    }
}
