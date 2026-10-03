using HockeyPlanner.Backend.Application.Policies;
using HockeyPlanner.Backend.Core.Enums;
using HockeyPlanner.Backend.Core.Exceptions;

namespace HockeyPlanner.Backend.IntegrationTests.Services;

[Trait("Category", "HP81")]
public sealed class CoreTeamRolePolicyTests
{
    // Columns: actor, manage, metadata (any target), change role, remove Member, remove Admin, remove Owner, leave.
    [Theory]
    [InlineData(TeamMemberRole.Owner, true, true, true, true, true, 400, 400)]
    [InlineData(TeamMemberRole.Admin, true, true, false, true, false, 400, 204)]
    [InlineData(TeamMemberRole.Member, false, false, false, false, false, 403, 204)]
    public void RoleMatrix(TeamMemberRole actor, bool manage, bool metadata, bool change,
        bool removeMember, bool removeAdmin, int removeOwner, int leave)
    {
        Check(() => CoreTeamRolePolicy.EnsureCanManage(actor), manage ? 204 : 403);
        Assert.Equal(manage, CoreTeamRolePolicy.CanManage(actor));
        foreach (var target in Enum.GetValues<TeamMemberRole>())
        {
            Check(() => CoreTeamRolePolicy.EnsureCanUpdateMember(actor, target, null), metadata ? 204 : 403);
            // Repeating the current role is metadata-only, preserving the approved base behavior.
            Check(() => CoreTeamRolePolicy.EnsureCanUpdateMember(actor, target, target), metadata ? 204 : 403);
        }
        Check(() => CoreTeamRolePolicy.EnsureCanUpdateMember(actor, TeamMemberRole.Member, TeamMemberRole.Admin), change ? 204 : 403);
        Check(() => CoreTeamRolePolicy.EnsureCanUpdateMember(actor, TeamMemberRole.Admin, TeamMemberRole.Member), change ? 204 : 403);
        Check(() => CoreTeamRolePolicy.EnsureCanRemove(actor, TeamMemberRole.Member), removeMember ? 204 : 403);
        Check(() => CoreTeamRolePolicy.EnsureCanRemove(actor, TeamMemberRole.Admin), removeAdmin ? 204 : 403);
        Check(() => CoreTeamRolePolicy.EnsureCanRemove(actor, TeamMemberRole.Owner), removeOwner);
        Check(() => CoreTeamRolePolicy.EnsureCanLeave(actor), leave);
        foreach (var nonOwner in new[] { TeamMemberRole.Member, TeamMemberRole.Admin })
        {
            Check(() => CoreTeamRolePolicy.EnsureCanUpdateMember(actor, nonOwner, TeamMemberRole.Owner), change ? 400 : 403);
            Check(() => CoreTeamRolePolicy.EnsureCanUpdateMember(actor, TeamMemberRole.Owner, nonOwner), change ? 400 : 403);
        }
        Check(() => CoreTeamRolePolicy.EnsureCanUpdateMember(actor, TeamMemberRole.Member, (TeamMemberRole)99), change ? 400 : 403);
    }

    [Fact]
    public void NonMemberCannotManage()
    {
        Assert.False(CoreTeamRolePolicy.CanManage(null));
        Assert.Throws<UnauthorizedException>(() => CoreTeamRolePolicy.EnsureCanManage(null));
    }

    private static void Check(Action action, int expected)
    {
        var error = Record.Exception(action);
        if (expected == 204) Assert.Null(error);
        else if (expected == 400) Assert.IsType<BusinessRuleException>(error);
        else Assert.IsType<UnauthorizedException>(error);
    }
}
