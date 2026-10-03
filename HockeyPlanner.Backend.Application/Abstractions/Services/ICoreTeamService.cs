using HockeyPlanner.Backend.Shared.Models.Teams;

namespace HockeyPlanner.Backend.Application.Abstractions.Services;

public interface ICoreTeamService
{
    Task<IReadOnlyCollection<TeamDto>> GetMyTeams(Guid actorUserId, CancellationToken cancellationToken);
    Task<IReadOnlyCollection<TeamDto>> GetPublicTeams(CancellationToken cancellationToken);
    Task<TeamDto> GetTeam(Guid id, bool viewerIsAuthenticated, Guid? viewerUserId, CancellationToken cancellationToken);
    Task<IReadOnlyCollection<TeamMemberDto>> GetTeamMembers(Guid id, bool viewerIsAuthenticated, Guid? viewerUserId, CancellationToken cancellationToken);
    Task<TeamDto> CreateTeam(Guid actorUserId, CreateTeamRequest request, CancellationToken cancellationToken);
    Task<TeamDto> UpdateTeam(Guid id, Guid actorUserId, UpdateTeamRequest request, CancellationToken cancellationToken);
    Task<TeamMemberDto> UpdateTeamMember(Guid id, Guid userId, Guid actorUserId, UpdateTeamMemberRequest request, CancellationToken cancellationToken);
    Task RemoveTeamMember(Guid id, Guid userId, Guid actorUserId, CancellationToken cancellationToken);
    Task<TeamDto> JoinByCode(Guid actorUserId, JoinTeamByCodeRequest request, CancellationToken cancellationToken);
    Task<TeamDto> JoinPublic(Guid id, Guid actorUserId, int? teamJerseyNumber, CancellationToken cancellationToken);
    Task LeaveTeam(Guid id, Guid actorUserId, CancellationToken cancellationToken);
    Task<TeamDto> UpdateMyTeamJerseyNumber(Guid id, Guid actorUserId, UpdateMyTeamJerseyNumberRequest request, CancellationToken cancellationToken);
}
