namespace HockeyPlanner.Backend.Core.Enums
{
    public enum AttendanceStatus : int
    {
        Pending = 1,      // Не ответил
        Confirmed = 2,    // Будет
        Declined = 3,     // Не придет
        Tentative = 4,    // Возможно
        Late = 5          // Опоздает
    }
}
