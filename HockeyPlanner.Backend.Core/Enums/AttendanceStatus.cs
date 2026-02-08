namespace HockeyPlanner.Backend.Core.Enums
{
    public enum AttendanceStatus : int
    {
        Pending = 1,      // Не ответил
        Confirmed = 2,    // Будет
        Declined = 3,     // Не придет
        Late = 4          // Опоздает
    }
}
