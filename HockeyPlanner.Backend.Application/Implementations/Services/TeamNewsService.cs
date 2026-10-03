using HockeyPlanner.Backend.Application.Abstractions.Services;
using HockeyPlanner.Backend.Application.Policies;
using HockeyPlanner.Backend.Core.Entities;
using HockeyPlanner.Backend.Core.Exceptions;
using HockeyPlanner.Backend.Core.Enums;
using HockeyPlanner.Backend.Infrastructure.Data;
using HockeyPlanner.Backend.Shared.Models.Teams;
using Microsoft.EntityFrameworkCore;

namespace HockeyPlanner.Backend.Application.Implementations.Services;

internal sealed class TeamNewsService(AppDbContext context, INotificationService notifications, TimeProvider timeProvider) : ITeamNewsService
{
    private readonly AppDbContext _context = context;
    private readonly INotificationService _notificationService = notifications;

    public async Task<IReadOnlyCollection<TeamNewsDto>> GetTeamNews(Guid id, bool viewerIsAuthenticated, Guid? actorUserId, CancellationToken cancellationToken)
    {
        var team = await _context.Teams.AsNoTracking()
            .Include(value => value.Memberships)
            .FirstOrDefaultAsync(value => value.Id == id, cancellationToken);
        if (team == null)
        {
            throw new NotFoundException("Команда не найдена.");
        }

        CoreTeamService.EnsureVisible(team, viewerIsAuthenticated, actorUserId);

        var canManage = actorUserId.HasValue && actorUserId.Value != Guid.Empty && await CanManageTeamAsync(id, actorUserId.Value, cancellationToken);

        var news = await _context.TeamNews
            .AsNoTracking()
            .Where(value => value.TeamId == id)
            .OrderByDescending(value => value.CreatedAt)
            .Take(50)
            .Select(value => new TeamNewsDto
            {
                Id = value.Id,
                TeamId = value.TeamId,
                TeamName = value.Team.Name,
                Title = value.Title,
                Body = value.Body,
                ImageUrl = value.ImageUrl,
                AuthorUserId = value.AuthorUserId,
                AuthorName = (value.AuthorUser.LastName + " " + value.AuthorUser.FirstName).Trim(),
                CreatedAt = value.CreatedAt,
                UpdatedAt = value.UpdatedAt,
                CanManage = canManage
            })
            .ToListAsync(cancellationToken);

        return news;
    }

    public async Task<IReadOnlyCollection<TeamNewsDto>> GetNewsFeed(Guid actorUserId, CancellationToken cancellationToken)
    {
        var userExists = await _context.Users.AsNoTracking().AnyAsync(user => user.Id == actorUserId, cancellationToken);
        if (!userExists)
        {
            throw new NotFoundException("Пользователь не найден.");
        }

        var memberships = await _context.TeamMemberships.AsNoTracking()
            .Where(value => value.UserId == actorUserId)
            .Select(value => new { value.TeamId, value.Role }).ToListAsync(cancellationToken);
        var manageableTeamIds = memberships.Where(value => CoreTeamRolePolicy.CanManage(value.Role))
            .Select(value => value.TeamId).ToArray();

        var news = await _context.TeamNews
            .AsNoTracking()
            .Where(value => value.Team.Memberships.Any(membership => membership.UserId == actorUserId))
            .OrderByDescending(value => value.CreatedAt)
            .Take(100)
            .Select(value => new TeamNewsDto
            {
                Id = value.Id,
                TeamId = value.TeamId,
                TeamName = value.Team.Name,
                Title = value.Title,
                Body = value.Body,
                ImageUrl = value.ImageUrl,
                AuthorUserId = value.AuthorUserId,
                AuthorName = (value.AuthorUser.LastName + " " + value.AuthorUser.FirstName).Trim(),
                CreatedAt = value.CreatedAt,
                UpdatedAt = value.UpdatedAt,
                CanManage = manageableTeamIds.Contains(value.TeamId)
            })
            .ToListAsync(cancellationToken);

        return news;
    }

    public async Task<TeamNewsDto> CreateTeamNews(
        Guid id,
        Guid actorUserId, CreateTeamNewsRequest request, CancellationToken cancellationToken)
    {
        var title = NormalizeNewsTitle(request.Title);
        var body = NormalizeNewsBody(request.Body);
        if (string.IsNullOrWhiteSpace(title) || string.IsNullOrWhiteSpace(body))
        {
            throw new BusinessRuleException("У новости должны быть название и текст.");
        }

        var membership = await _context.TeamMemberships
            .AsNoTracking()
            .FirstOrDefaultAsync(value => value.TeamId == id && value.UserId == actorUserId, cancellationToken);

        if (membership == null || !CoreTeamRolePolicy.CanManage(membership.Role))
        {
            throw new UnauthorizedException("Недостаточно прав");
        }

        var user = await _context.Users.AsNoTracking().FirstOrDefaultAsync(value => value.Id == actorUserId, cancellationToken);
        if (user == null)
        {
            throw new NotFoundException("Пользователь не найден.");
        }

        var news = new TeamNews
        {
            TeamId = id,
            AuthorUserId = actorUserId,
            Title = title,
            Body = body,
            ImageUrl = NormalizeUrl(request.ImageUrl),
            CreatedAt = timeProvider.GetUtcNow().UtcDateTime,
            UpdatedAt = timeProvider.GetUtcNow().UtcDateTime
        };

        // The existing M6 outbox joins this transaction and never delivers here.
        await using var transaction = await _context.Database.BeginTransactionAsync(cancellationToken);
        await _context.TeamNews.AddAsync(news, cancellationToken);
        await _context.SaveChangesAsync(cancellationToken);

        if (request.SendNotification)
        {
            await _notificationService.NotifyTeamAsync(
                id,
                NotificationType.TeamNewsCreated,
                NotificationCategory.TeamNews,
                title,
                body,
                $"/teams/{id}", cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
        return new TeamNewsDto
        {
            Id = news.Id,
            TeamId = news.TeamId,
            TeamName = string.Empty,
            Title = news.Title,
            Body = news.Body,
            ImageUrl = news.ImageUrl,
            AuthorUserId = news.AuthorUserId,
            AuthorName = $"{user.LastName} {user.FirstName}".Trim(),
            CreatedAt = news.CreatedAt,
            UpdatedAt = news.UpdatedAt,
            CanManage = true
        };
    }

    public async Task<TeamNewsDto> UpdateTeamNews(
        Guid teamId,
        Guid newsId,
        Guid actorUserId, UpdateTeamNewsRequest request, CancellationToken cancellationToken)
    {
        if (!await CanManageTeamAsync(teamId, actorUserId, cancellationToken))
        {
            throw new UnauthorizedException("Недостаточно прав");
        }

        var title = NormalizeNewsTitle(request.Title);
        var body = NormalizeNewsBody(request.Body);
        if (string.IsNullOrWhiteSpace(title) || string.IsNullOrWhiteSpace(body))
        {
            throw new BusinessRuleException("У новости должны быть название и текст.");
        }

        var news = await _context.TeamNews
            .Include(value => value.Team)
            .Include(value => value.AuthorUser)
            .FirstOrDefaultAsync(value => value.Id == newsId && value.TeamId == teamId, cancellationToken);

        if (news == null)
        {
            throw new NotFoundException("Новость не найдена.");
        }

        news.Title = title;
        news.Body = body;
        news.ImageUrl = NormalizeUrl(request.ImageUrl);
        news.UpdatedAt = timeProvider.GetUtcNow().UtcDateTime;
        await _context.SaveChangesAsync(cancellationToken);

        return new TeamNewsDto
        {
            Id = news.Id,
            TeamId = news.TeamId,
            TeamName = news.Team.Name,
            Title = news.Title,
            Body = news.Body,
            ImageUrl = news.ImageUrl,
            AuthorUserId = news.AuthorUserId,
            AuthorName = $"{news.AuthorUser.LastName} {news.AuthorUser.FirstName}".Trim(),
            CreatedAt = news.CreatedAt,
            UpdatedAt = news.UpdatedAt,
            CanManage = true
        };
    }

    public async Task DeleteTeamNews(Guid teamId, Guid newsId, Guid actorUserId, CancellationToken cancellationToken)
    {
        if (!await CanManageTeamAsync(teamId, actorUserId, cancellationToken))
        {
            throw new UnauthorizedException("Недостаточно прав");
        }

        var news = await _context.TeamNews.FirstOrDefaultAsync(value => value.Id == newsId && value.TeamId == teamId, cancellationToken);
        if (news == null)
        {
            throw new NotFoundException("Новость не найдена.");
        }

        _context.TeamNews.Remove(news);
        await _context.SaveChangesAsync(cancellationToken);

        return;
    }

    private static string NormalizeNewsTitle(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var normalized = NormalizeName(value);
        return normalized.Length > 120 ? normalized[..120] : normalized;
    }

    private static string NormalizeNewsBody(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var normalized = value.Trim();
        return normalized.Length > 2000 ? normalized[..2000] : normalized;
    }

    private static string NormalizeName(string value)
    {
        var parts = value
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return string.Join(" ", parts);
    }

    private static string? NormalizeUrl(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var normalized = value.Trim();
        return normalized.Length > 500 ? normalized[..500] : normalized;
    }

    private async Task<bool> CanManageTeamAsync(Guid teamId, Guid actorUserId, CancellationToken cancellationToken)
    {
        var role = await _context.TeamMemberships.AsNoTracking()
            .Where(value => value.TeamId == teamId && value.UserId == actorUserId)
            .Select(value => (TeamMemberRole?)value.Role).SingleOrDefaultAsync(cancellationToken);
        return CoreTeamRolePolicy.CanManage(role);
    }
}
