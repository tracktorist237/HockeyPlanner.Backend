using HockeyPlanner.Backend.Application;
using HockeyPlanner.Backend.Infrastructure;
using HockeyPlanner.Backend.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace HockeyPlanner.Backend.WebAPI
{
    public class Program
    {
        public static void Main(string[] args)
        {
            var builder = WebApplication.CreateBuilder(args);

            // Настройка порта для Render
            var port = Environment.GetEnvironmentVariable("PORT") ?? "8080";
            builder.WebHost.UseUrls($"http://*:{port}");

            // Add services to the container.
            builder.Services.AddEndpointsApiExplorer();
            builder.Services.AddSwaggerGen();
            builder.Services.AddControllers();

            // Настройка CORS для разработки и продакшена
            builder.Services.AddCors(options =>
            {
                options.AddPolicy("DevCors", policy =>
                {
                    policy
                        .WithOrigins(
                            "http://localhost:3000", // локальный React
                            "https://hockey-planner-frontend.onrender.com",// продакшен фронтенд
                            "https://hockey-planner-test.onrender.com/" // тестовый фронтенд
                        )
                        .AllowAnyHeader()
                        .AllowAnyMethod()
                        .AllowCredentials();
                });

                options.AddPolicy("ProdCors", policy =>
                {
                    policy.WithOrigins(
                        "http://localhost:3000",
                        "https://hockey-planner.onrender.com",
                        "https://hockey-planner-test.onrender.com/"
                        )
                    .AllowAnyHeader()
                    .AllowAnyMethod()
                    .AllowCredentials();
                });
            });

            // Настройка базы данных с переменными окружения
            var connectionString = builder.Configuration.GetConnectionString("DefaultConnection");

            // Заменяем переменные окружения в строке подключения
            if (!string.IsNullOrEmpty(connectionString))
            {
                connectionString = connectionString
                    .Replace("${DB_HOST}", Environment.GetEnvironmentVariable("DB_HOST") ?? "")
                    .Replace("${DB_PORT}", Environment.GetEnvironmentVariable("DB_PORT") ?? "5432")
                    .Replace("${DB_NAME}", Environment.GetEnvironmentVariable("DB_NAME") ?? "")
                    .Replace("${DB_USER}", Environment.GetEnvironmentVariable("DB_USER") ?? "")
                    .Replace("${DB_PASSWORD}", Environment.GetEnvironmentVariable("DB_PASSWORD") ?? "");
            }

            // Передаем строку подключения в инфраструктуру через конфигурацию
            if (!string.IsNullOrEmpty(connectionString))
            {
                builder.Configuration["ConnectionStrings:DefaultConnection"] = connectionString;
            }

            builder.Services.AddInfrastructure(builder.Configuration);
            builder.Services.AddApplication();

            var app = builder.Build();

            // Применяем миграции автоматически при запуске (только в Production)
            if (!app.Environment.IsDevelopment())
            {
                using (var scope = app.Services.CreateScope())
                {
                    var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                    try
                    {
                        dbContext.Database.Migrate();
                        Console.WriteLine("Миграции успешно применены");
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"Ошибка при применении миграций: {ex.Message}");
                    }
                }
            }

            // Configure the HTTP request pipeline.
            if (app.Environment.IsDevelopment())
            {
                app.UseSwagger();
                app.UseSwaggerUI();
                app.MapOpenApi();
            }
            else
            {
                // В продакшене используем HTTPS редирект
                app.UseHttpsRedirection();
            }

            // Health check endpoint
            app.MapGet("/health", () => Results.Ok(new
            {
                status = "Healthy",
                timestamp = DateTime.UtcNow,
                environment = app.Environment.EnvironmentName
            }));

            app.UseAuthorization();

            // Используем CORS в зависимости от окружения
            if (app.Environment.IsDevelopment())
            {
                app.UseCors("DevCors");
                Console.WriteLine("Используется DevCors");
            }
            else
            {
                app.UseCors("ProdCors");
                Console.WriteLine("Используется ProdCors");
            }

            app.MapControllers();

            Console.WriteLine($"Приложение запущено на порту {port}");
            Console.WriteLine($"Окружение: {app.Environment.EnvironmentName}");

            app.Run();
        }
    }
}