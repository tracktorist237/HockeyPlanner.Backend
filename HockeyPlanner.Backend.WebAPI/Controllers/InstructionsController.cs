using HockeyPlanner.Backend.Core.Entities;
using HockeyPlanner.Backend.Infrastructure.Data;
using HockeyPlanner.Backend.WebAPI.Models.Instructions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace HockeyPlanner.Backend.WebAPI.Controllers
{
    [ApiController]
    [Route("api/instructions")]
    public class InstructionsController : ControllerBase
    {
        private readonly AppDbContext _context;

        public InstructionsController(AppDbContext context)
        {
            _context = context;
        }

        [HttpGet]
        public async Task<ActionResult<IReadOnlyCollection<InstructionListItemDto>>> GetPublishedInstructions(CancellationToken cancellationToken)
        {
            var articles = await _context.InstructionArticles
                .AsNoTracking()
                .Where(article => article.IsPublished)
                .OrderBy(article => article.SortOrder)
                .ThenBy(article => article.Title)
                .ToListAsync(cancellationToken);

            return Ok(articles.Select(MapListItem).ToList());
        }

        [HttpGet("{slug}")]
        public async Task<ActionResult<InstructionArticleDto>> GetPublishedInstruction(string slug, CancellationToken cancellationToken)
        {
            var normalizedSlug = slug.Trim().ToLowerInvariant();
            var article = await _context.InstructionArticles
                .AsNoTracking()
                .FirstOrDefaultAsync(value => value.Slug == normalizedSlug && value.IsPublished, cancellationToken);

            return article == null ? NotFound() : Ok(MapArticle(article));
        }

        internal static InstructionListItemDto MapListItem(InstructionArticle article)
        {
            return new InstructionListItemDto
            {
                Id = article.Id,
                Slug = article.Slug,
                Title = article.Title,
                Summary = article.Summary,
                ImageUrl = article.ImageUrl,
                SortOrder = article.SortOrder,
                PublishedAt = article.PublishedAt
            };
        }

        internal static InstructionArticleDto MapArticle(InstructionArticle article)
        {
            return new InstructionArticleDto
            {
                Id = article.Id,
                Slug = article.Slug,
                Title = article.Title,
                Summary = article.Summary,
                Content = article.Content,
                ImageUrl = article.ImageUrl,
                IsPublished = article.IsPublished,
                SortOrder = article.SortOrder,
                PublishedAt = article.PublishedAt,
                CreatedAt = article.CreatedAt,
                UpdatedAt = article.UpdatedAt,
                CreatedByUserId = article.CreatedByUserId
            };
        }
    }
}
