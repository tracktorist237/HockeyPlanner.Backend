using HockeyPlanner.Backend.Core.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace HockeyPlanner.Backend.Infrastructure.Data.Configurations
{
    public class GoalieRequestConfiguration : IEntityTypeConfiguration<GoalieRequest>
    {
        public void Configure(EntityTypeBuilder<GoalieRequest> builder)
        {
            builder.HasKey(request => request.Id);

            builder.Property(request => request.NeededCount)
                .IsRequired();

            builder.Property(request => request.PriceText)
                .HasMaxLength(120);

            builder.Property(request => request.Description)
                .HasMaxLength(2000);

            builder.HasOne(request => request.Event)
                .WithOne(e => e.GoalieRequest)
                .HasForeignKey<GoalieRequest>(request => request.EventId)
                .OnDelete(DeleteBehavior.Cascade);

            builder.HasOne(request => request.Team)
                .WithMany(team => team.GoalieRequests)
                .HasForeignKey(request => request.TeamId)
                .OnDelete(DeleteBehavior.SetNull);

            builder.HasOne(request => request.CreatedByUser)
                .WithMany()
                .HasForeignKey(request => request.CreatedByUserId)
                .OnDelete(DeleteBehavior.Restrict);

            builder.HasIndex(request => request.EventId)
                .IsUnique();

            builder.HasIndex(request => request.TeamId);
            builder.HasIndex(request => request.Status);
            builder.HasIndex(request => request.Visibility);
        }
    }
}
