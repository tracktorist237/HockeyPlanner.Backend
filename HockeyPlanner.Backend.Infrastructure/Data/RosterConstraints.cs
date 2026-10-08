using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace HockeyPlanner.Backend.Infrastructure.Data;

// Only roster membership violations may become a controlled roster conflict.
public static class RosterConstraints
{
    public const string UserIndex = "ux_players_event_user";
    public const string GuestIndex = "ux_players_event_guest";
    public const string ConflictMessage = "Игрок или гость уже включён в состав мероприятия";

    public static bool IsDuplicate(DbUpdateException exception) =>
        exception.InnerException is PostgresException
        {
            SqlState: PostgresErrorCodes.UniqueViolation,
            ConstraintName: UserIndex or GuestIndex
        };
}
