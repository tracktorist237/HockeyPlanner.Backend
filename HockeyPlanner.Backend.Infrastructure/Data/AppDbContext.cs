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