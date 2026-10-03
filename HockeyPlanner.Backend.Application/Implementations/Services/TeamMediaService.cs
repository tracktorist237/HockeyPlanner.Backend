using HockeyPlanner.Backend.Application.Abstractions.Services;
using HockeyPlanner.Backend.Application.Policies;
using HockeyPlanner.Backend.Core.Entities;
using HockeyPlanner.Backend.Core.Enums;
using HockeyPlanner.Backend.Core.Exceptions;
using HockeyPlanner.Backend.Infrastructure.Data;
using HockeyPlanner.Backend.Shared.Models.Teams;
using Microsoft.EntityFrameworkCore;

namespace HockeyPlanner.Backend.Application.Implementations.Services;

internal sealed class TeamMediaService(AppDbContext context, TimeProvider timeProvider) : ITeamMediaService
{
    private (Team Team, TeamMembership Actor)? _authorizedMedia;

    public async Task EnsureCanUploadTeamMedia(Guid id, Guid actorUserId, CancellationToken cancellationToken)
    {
        _authorizedMedia = null;
        _authorizedMedia = await LoadManageableTeam(id, actorUserId, cancellationToken);
    }

    public async Task EnsureCanUploadNewsImage(Guid id, Guid actorUserId, CancellationToken cancellationToken)
    {
        // Historical news-image contract: missing membership/team returns 403.
        var role = await context.TeamMemberships.AsNoTracking()
            .Where(value => value.TeamId == id && value.UserId == actorUserId)
            .Select(value => (TeamMemberRole?)value.Role).SingleOrDefaultAsync(cancellationToken);
        CoreTeamRolePolicy.EnsureCanManage(role);
    }

    public async Task<TeamDto> SaveTeamMedia(Guid id, Guid actorUserId, string publicUrl, bool isCover, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        // Reuse the pre-storage snapshot, like the original controller. Reloading
        // memberships here can merge a replacement PK into the tracked navigation.
        if (_authorizedMedia is not { } snapshot || snapshot.Team.Id != id || snapshot.Actor.UserId != actorUserId)
            throw new InvalidOperationException("Team media must be authorized before saving.");
        var (team, actor) = snapshot;
        if (isCover) team.CoverImageUrl = publicUrl;
        else team.AvatarUrl = publicUrl;
        team.UpdatedAt = timeProvider.GetUtcNow().UtcDateTime;
        await context.SaveChangesAsync(cancellationToken);
        // Preserve the existing upload DTO, including its null team jersey number.
        return CoreTeamService.ToDto(team, actor.Role, actor.BadgeTitle, team.InviteCode);
    }

    private async Task<(Team Team, TeamMembership Actor)> LoadManageableTeam(Guid id, Guid actorUserId, CancellationToken cancellationToken)
    {
        var team = await context.Teams.Include(value => value.Memberships)
            .FirstOrDefaultAsync(value => value.Id == id, cancellationToken)
            ?? throw new NotFoundException("Команда не найдена.");
        var actor = team.Memberships.FirstOrDefault(value => value.UserId == actorUserId);
        CoreTeamRolePolicy.EnsureCanManage(actor?.Role);
        return (team, actor!);
    }
}
