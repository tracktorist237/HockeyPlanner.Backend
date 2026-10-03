using HockeyPlanner.Backend.Core.Enums;
using HockeyPlanner.Backend.Core.Exceptions;

namespace HockeyPlanner.Backend.Application.Policies;

/// <summary>Core team permissions and the immutable Owner boundary. Ownership transfer is unsupported.</summary>
public static class CoreTeamRolePolicy
{
    public static bool CanManage(TeamMemberRole? actor) =>
        actor is TeamMemberRole.Owner or TeamMemberRole.Admin;

    public static void EnsureCanManage(TeamMemberRole? actor)
    {
        if (!CanManage(actor)) throw new UnauthorizedException("Недостаточно прав");
    }

    public static void EnsureCanUpdateMember(TeamMemberRole actor, TeamMemberRole target, TeamMemberRole? requested)
    {
        EnsureCanManage(actor);
        if (requested.HasValue && requested != target)
        {
            if (actor != TeamMemberRole.Owner) throw new UnauthorizedException("Недостаточно прав");
            if (target == TeamMemberRole.Owner)
                throw new BusinessRuleException("Нельзя изменить роль владельца команды.");
            if (requested == TeamMemberRole.Owner)
                throw new BusinessRuleException("Передача владения пока не поддерживается.");
            if (requested is not (TeamMemberRole.Admin or TeamMemberRole.Member))
                throw new BusinessRuleException("Недопустимая роль участника команды.");
        }

        // HP-81 explicitly preserves the base metadata behavior: Admin may edit
        // badge/number on any role. Only actual role changes require Owner.
    }

    public static void EnsureCanRemove(TeamMemberRole actor, TeamMemberRole target)
    {
        EnsureCanManage(actor);
        if (target == TeamMemberRole.Owner)
            throw new BusinessRuleException("Нельзя удалить владельца команды.");
        if (actor == TeamMemberRole.Admin && target != TeamMemberRole.Member)
            throw new UnauthorizedException("Недостаточно прав");
    }

    public static void EnsureCanLeave(TeamMemberRole actor)
    {
        if (actor == TeamMemberRole.Owner)
            throw new BusinessRuleException("Владелец не может покинуть команду, пока в ней есть другие участники.");
    }
}
