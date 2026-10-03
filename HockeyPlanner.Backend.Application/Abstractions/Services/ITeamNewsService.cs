using HockeyPlanner.Backend.Shared.Models.Teams;

namespace HockeyPlanner.Backend.Application.Abstractions.Services;

public interface ITeamNewsService
{
    Task<IReadOnlyCollection<TeamNewsDto>> GetTeamNews(Guid id, bool viewerIsAuthenticated, Guid? actorUserId, CancellationToken cancellationToken);
    Task<IReadOnlyCollection<TeamNewsDto>> GetNewsFeed(Guid actorUserId, CancellationToken cancellationToken);
    Task<TeamNewsDto> CreateTeamNews(Guid id, Guid actorUserId, CreateTeamNewsRequest request, CancellationToken cancellationToken);
    Task<TeamNewsDto> UpdateTeamNews(Guid teamId, Guid newsId, Guid actorUserId, UpdateTeamNewsRequest request, CancellationToken cancellationToken);
    Task DeleteTeamNews(Guid teamId, Guid newsId, Guid actorUserId, CancellationToken cancellationToken);
}
