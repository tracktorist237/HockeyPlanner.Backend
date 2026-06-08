using HockeyPlanner.Backend.Core.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace HockeyPlanner.Backend.Infrastructure.Data.Configurations
{
    public class InstructionArticleConfiguration : IEntityTypeConfiguration<InstructionArticle>
    {
        public void Configure(EntityTypeBuilder<InstructionArticle> builder)
        {
            builder.HasKey(article => article.Id);

            builder.Property(article => article.Slug)
                .IsRequired()
                .HasMaxLength(120);

            builder.Property(article => article.Title)
                .IsRequired()
                .HasMaxLength(180);

            builder.Property(article => article.Summary)
                .HasMaxLength(500);

            builder.Property(article => article.Content)
                .IsRequired()
                .HasMaxLength(12000);

            builder.Property(article => article.ImageUrl)
                .HasMaxLength(1000);

            builder.Property(article => article.IsPublished)
                .IsRequired();

            builder.Property(article => article.SortOrder)
                .IsRequired();

            builder.HasOne(article => article.CreatedByUser)
                .WithMany()
                .HasForeignKey(article => article.CreatedByUserId)
                .OnDelete(DeleteBehavior.SetNull);

            builder.HasIndex(article => article.Slug)
                .IsUnique();

            builder.HasIndex(article => article.IsPublished);
            builder.HasIndex(article => article.SortOrder);
            builder.HasIndex(article => article.PublishedAt);
        }
    }
}
