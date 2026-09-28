using HockeyPlanner.Backend.Core.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace HockeyPlanner.Backend.Infrastructure.Data.Configurations;

public sealed class LeagueNotificationBatchConfiguration : IEntityTypeConfiguration<LeagueNotificationBatch>
{
    public void Configure(EntityTypeBuilder<LeagueNotificationBatch> builder)
    {
        builder.HasKey(value => value.Id);
        builder.Property(value => value.ChangesJson).HasColumnType("jsonb");
        builder.HasOne<Team>().WithMany().HasForeignKey(value => value.TeamId).OnDelete(DeleteBehavior.Cascade);
        builder.HasIndex(value => value.CompletedAt).HasDatabaseName("ix_league_notification_batches_pending");
    }
}
