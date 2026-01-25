using HockeyPlanner.Backend.Core.Entities.Base;
using HockeyPlanner.Backend.Core.Enums;

namespace HockeyPlanner.Backend.Core.Entities
{
    public class User : Entity
    {
        public string FirstName { get; set; } = string.Empty;
        public string LastName { get; set; } = string.Empty;
        public string? Email { get; set; }
        public string? Phone { get; set; }
        public string? PhotoUrl { get; set; }

        // Роль в системе
        public UserRole Role { get; set; }

        // Хоккейная информация
        public int? JerseyNumber { get; set; }
        public Position? PrimaryPosition { get; set; }
        public Position? SecondaryPosition { get; set; }
        public Handedness? Handedness { get; set; }

        // Вычисляемые свойства
        public string FullName => $"{LastName} {FirstName}";
    }
}

