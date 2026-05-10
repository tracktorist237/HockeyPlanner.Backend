using HockeyPlanner.Backend.Core.Entities.Base;
using HockeyPlanner.Backend.Core.Enums;

namespace HockeyPlanner.Backend.Core.Entities
{
    public class User : Entity
    {
        public string FirstName { get; set; } = string.Empty;
        public string LastName { get; set; } = string.Empty;
        public string? Email { get; set; }
        public bool EmailConfirmed { get; set; }
        public string? Phone { get; set; }
        public string? PasswordHash { get; set; }
        public DateTime? PasswordUpdatedAt { get; set; }
        public string? PhotoUrl { get; set; }
        public Guid? SpbhlPlayerId { get; set; }

        // Роль в системе
        public UserRole Role { get; set; }

        // Хоккейная информация
        public int? JerseyNumber { get; set; }
        public Position? PrimaryPosition { get; set; }
        public Handedness? Handedness { get; set; }
        public int? Height { get; set; }
        public int? Weight { get; set; }
        public DateTime? BirthDate { get; set; }

        // Вычисляемые свойства
        public string FullName => $"{LastName} {FirstName}";
    }
}

