using HockeyPlanner.Backend.Core.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace HockeyPlanner.Backend.Infrastructure.Data.Configurations
{
    public class GoalieApplicationConfiguration : IEntityTypeConfiguration<GoalieApplication>
    {
        public void Configure(EntityTypeBuilder<GoalieApplication> builder)
        {
            builder.HasKey(application => application.Id);

            builder.Property(application => application.Message)
                .HasMaxLength(1000);

            builder.HasOne(application => application.GoalieRequest)
                .WithMany(request => request.Applications)
                .HasForeignKey(application => application.GoalieRequestId)
                .OnDelete(DeleteBehavior.Cascade);

            builder.HasOne(application => application.GoalieUser)
                .WithMany()
                .HasForeignKey(application => application.GoalieUserId)
                .OnDelete(DeleteBehavior.Cascade);

            builder.HasIndex(application => application.GoalieRequestId);
            builder.HasIndex(application => application.GoalieUserId);
            builder.HasIndex(application => application.Status);
            builder.HasIndex(application => new { application.GoalieRequestId, application.GoalieUserId })
                .IsUnique();
        }
    }
}
