namespace HockeyPlanner.Backend.Core.Enums
{
    public enum EventStatus
    {
        Scheduled = 1,    // Запланировано
        InProgress = 2,   // В процессе
        Completed = 3,    // Завершено
        Cancelled = 4,    // Отменено
        Rescheduled = 5   // Перенесено
    }
}
