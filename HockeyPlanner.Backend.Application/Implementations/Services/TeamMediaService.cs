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
    public async Task EnsureCanUploadTeamMedia(Guid id, Guid actorUserId, CancellationToken cancellationToken) =>
        _ = await LoadManageableTeam(id, actorUserId, cancellationToken);

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
        var team = await LoadManageableTeam(id, actorUserId, cancellationToken);
        if (isCover) team.CoverImageUrl = publicUrl;
        else team.AvatarUrl = publicUrl;
        team.UpdatedAt = timeProvider.GetUtcNow().UtcDateTime;
        await context.SaveChangesAsync(cancellationToken);
        var actor = team.Memberships.Single(value => value.UserId == actorUserId);
        // Preserve the existing upload DTO, including its null team jersey number.
        return CoreTeamService.ToDto(team, actor.Role, actor.BadgeTitle, team.InviteCode);
    }

    private async Task<Team> LoadManageableTeam(Guid id, Guid actorUserId, CancellationToken cancellationToken)
    {
        var team = await context.Teams.Include(value => value.Memberships)
            .FirstOrDefaultAsync(value => value.Id == id, cancellationToken)
            ?? throw new NotFoundException("Команда не найдена.");
        CoreTeamRolePolicy.EnsureCanManage(team.Memberships.FirstOrDefault(value => value.UserId == actorUserId)?.Role);
        return team;
    }
}
