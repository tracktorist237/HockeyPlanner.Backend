using HockeyPlanner.Backend.Core.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace HockeyPlanner.Backend.Infrastructure.Data.Configurations
{
    public class UniformColorConfiguration : IEntityTypeConfiguration<UniformColor>
    {
        public void Configure(EntityTypeBuilder<UniformColor> builder)
        {
            builder.HasKey(x => x.Id);

            builder.Property(x => x.Name)
                .IsRequired()
                .HasMaxLength(200);

            builder.Property(x => x.ImageUrl)
                .IsRequired()
                .HasMaxLength(1000);

            builder.Property(x => x.CreatedByUserId)
                .IsRequired();

            builder.HasIndex(x => x.Name);
        }
    }
}
