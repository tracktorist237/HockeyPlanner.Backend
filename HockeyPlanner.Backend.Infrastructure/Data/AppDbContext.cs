using HockeyPlanner.Backend.Core.Entities;
using HockeyPlanner.Backend.Core.Enums;
using Microsoft.EntityFrameworkCore;

namespace HockeyPlanner.Backend.Infrastructure.Data
{
    public class AppDbContext : DbContext
    {
        public AppDbContext(DbContextOptions<AppDbContext> options)
            : base(options) { }

        // DbSets
        public DbSet<ScheduledEvent> Events { get; set; }
        public DbSet<Attendance> Attendances { get; set; }
        public DbSet<Line> Lines { get; set; }
        public DbSet<Player> Players { get; set; }
        public DbSet<User> Users { get; set; }
        public DbSet<PushSubscription> PushSubscriptions { get; set; }
        public DbSet<Exercise> Exercises { get; set; }
        public DbSet<ScheduledEventExercise> ScheduledEventExercises { get; set; }
        public DbSet<UniformColor> UniformColors { get; set; }
        public DbSet<Team> Teams { get; set; }
        public DbSet<TeamMembership> TeamMemberships { get; set; }
        public DbSet<TeamNews> TeamNews { get; set; }
        public DbSet<GoalieRequest> GoalieRequests { get; set; }
        public DbSet<GoalieApplication> GoalieApplications { get; set; }
        public DbSet<RefreshToken> RefreshTokens { get; set; }
        public DbSet<EmailConfirmationToken> EmailConfirmationTokens { get; set; }
        public DbSet<PasswordResetToken> PasswordResetTokens { get; set; }

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);

            // Регистрируем все конфигурации из сборки
            modelBuilder.ApplyConfigurationsFromAssembly(typeof(AppDbContext).Assembly);

            // Опционально: snake_case для PostgreSQL
            ApplySnakeCaseNaming(modelBuilder);
        }

        private static void ApplySnakeCaseNaming(ModelBuilder modelBuilder)
        {
            foreach (var entity in modelBuilder.Model.GetEntityTypes())
            {
                // Имя таблицы
                var tableName = entity.GetTableName();
                if (!string.IsNullOrEmpty(tableName))
                {
                    entity.SetTableName(ToSnakeCase(tableName));
                }

                // Имена колонок
                foreach (var property in entity.GetProperties())
                {
                    property.SetColumnName(ToSnakeCase(property.Name));
                }

                // Имена ключей
                foreach (var key in entity.GetKeys())
                {
                    var keyName = key.GetName();
                    if (!string.IsNullOrEmpty(keyName))
                    {
                        key.SetName(ToSnakeCase(keyName));
                    }
                }

                // Имена индексов
                foreach (var index in entity.GetIndexes())
                {
                    var indexName = index.GetDatabaseName();
                    if (!string.IsNullOrEmpty(indexName))
                    {
                        index.SetDatabaseName(ToSnakeCase(indexName));
                    }
                }
            }
        }

        private static string ToSnakeCase(string text)
        {
            if (string.IsNullOrEmpty(text)) return text;

            return string.Concat(text.Select((x, i) =>
                i > 0 && char.IsUpper(x) ? "_" + x : x.ToString()))
                .ToLower();
        }
    }
}
