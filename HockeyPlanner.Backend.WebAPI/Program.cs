using HockeyPlanner.Backend.Application;
using HockeyPlanner.Backend.Infrastructure;
using HockeyPlanner.Backend.Infrastructure.Data;
using HockeyPlanner.Backend.WebAPI.Options;
using HockeyPlanner.Backend.WebAPI.Services;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text;

namespace HockeyPlanner.Backend.WebAPI
{
    public class Program
    {
        public static void Main(string[] args)
        {
            var builder = WebApplication.CreateBuilder(args);
            builder.Logging.ClearProviders();
            builder.Logging.AddConsole();
            builder.Logging.AddDebug();

            // Настройка порта для Render
            var port = Environment.GetEnvironmentVariable("PORT") ?? "8080";
            builder.WebHost.UseUrls($"http://*:{port}");

            // Add services to the container.
            builder.Services.AddEndpointsApiExplorer();
            builder.Services.AddSwaggerGen();
            builder.Services.AddControllers();
            builder.Services.AddHttpClient();
            var jwtOptions = builder.Configuration.GetSection("Jwt").Get<JwtOptions>() ?? new JwtOptions();
            jwtOptions.SigningKey = Environment.GetEnvironmentVariable("JWT_SIGNING_KEY")
                ?? jwtOptions.SigningKey
                ?? string.Empty;
            if (string.IsNullOrWhiteSpace(jwtOptions.SigningKey))
            {
                if (!builder.Environment.IsDevelopment())
                {
                    throw new InvalidOperationException("JWT_SIGNING_KEY is required outside Development.");
                }

                jwtOptions.SigningKey = "dev-only-hockey-planner-jwt-signing-key-change-in-production";
            }
            builder.Configuration["Jwt:SigningKey"] = jwtOptions.SigningKey;
            builder.Services.Configure<JwtOptions>(builder.Configuration.GetSection("Jwt"));

            builder.Configuration["Email:SmtpHost"] = Environment.GetEnvironmentVariable("SMTP_HOST")
                ?? builder.Configuration["SMTP_HOST"]
                ?? builder.Configuration["Email:SmtpHost"];
            builder.Configuration["Email:SmtpPort"] = Environment.GetEnvironmentVariable("SMTP_PORT")
                ?? builder.Configuration["SMTP_PORT"]
                ?? builder.Configuration["Email:SmtpPort"];
            builder.Configuration["Email:SmtpUser"] = Environment.GetEnvironmentVariable("SMTP_USER")
                ?? builder.Configuration["SMTP_USER"]
                ?? builder.Configuration["Email:SmtpUser"];
            builder.Configuration["Email:SmtpPassword"] = Environment.GetEnvironmentVariable("SMTP_PASSWORD")
                ?? builder.Configuration["SMTP_PASSWORD"]
                ?? builder.Configuration["Email:SmtpPassword"];
            builder.Configuration["Email:FromEmail"] = Environment.GetEnvironmentVariable("SMTP_FROM_EMAIL")
                ?? builder.Configuration["SMTP_FROM_EMAIL"]
                ?? builder.Configuration["Email:FromEmail"];
            builder.Configuration["Email:FromName"] = Environment.GetEnvironmentVariable("SMTP_FROM_NAME")
                ?? builder.Configuration["SMTP_FROM_NAME"]
                ?? builder.Configuration["Email:FromName"];
            builder.Configuration["Email:FrontendBaseUrl"] = Environment.GetEnvironmentVariable("FRONTEND_BASE_URL")
                ?? builder.Configuration["FRONTEND_BASE_URL"]
                ?? builder.Configuration["Email:FrontendBaseUrl"];
            builder.Services.Configure<EmailOptions>(builder.Configuration.GetSection("Email"));

            builder.Services
                .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
                .AddJwtBearer(options =>
                {
                    options.TokenValidationParameters = new TokenValidationParameters
                    {
                        ValidateIssuer = true,
                        ValidIssuer = jwtOptions.Issuer,
                        ValidateAudience = true,
                        ValidAudience = jwtOptions.Audience,
                        ValidateIssuerSigningKey = true,
                        IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtOptions.SigningKey)),
                        ValidateLifetime = true,
                        ClockSkew = TimeSpan.FromSeconds(30)
                    };
                });
            builder.Services.AddAuthorization();
            builder.Services.AddScoped<IAuthTokenService, AuthTokenService>();
            builder.Services.AddScoped<IAuthEmailSender>(provider =>
            {
                var emailOptions = provider
                    .GetRequiredService<Microsoft.Extensions.Options.IOptions<EmailOptions>>()
                    .Value;

                if (string.IsNullOrWhiteSpace(emailOptions.SmtpHost))
                {
                    return ActivatorUtilities.CreateInstance<LoggingAuthEmailSender>(provider);
                }

                return ActivatorUtilities.CreateInstance<SmtpAuthEmailSender>(provider);
            });
            builder.Services.AddHttpClient(nameof(ImageKitUploader), client =>
            {
                client.Timeout = TimeSpan.FromSeconds(30);
                client.DefaultRequestHeaders.ConnectionClose = false;
                client.DefaultRequestHeaders.ExpectContinue = false;
                client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("HockeyPlanner", "1.0"));
            });
            builder.Services.AddScoped<IImageKitUploader, ImageKitUploader>();
            builder.Services.AddScoped<ISpbhlPlayerSearchService, SpbhlPlayerSearchService>();
            builder.Services.AddScoped<IWebPushService, WebPushService>();
            builder.Services.AddHostedService<BirthdayPushHostedService>();

            // Настройка CORS для разработки и продакшена
            builder.Services.AddCors(options =>
            {
                options.AddPolicy("DevCors", policy =>
                {
                    policy
                        .WithOrigins(
                            "http://localhost:3000", // локальный React
                            "https://hockey-planner-frontend.onrender.com", // продакшен фронтенд
                            "https://hockey-planner-test.onrender.com" // тестовый фронтенд
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
                        "https://hockey-planner-test.onrender.com"
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
            var logger = app.Services.GetRequiredService<ILogger<Program>>();

            // Применяем миграции автоматически при запуске (только в Production)
            if (!app.Environment.IsDevelopment())
            {
                using var scope = app.Services.CreateScope();
                var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                try
                {
                    dbContext.Database.Migrate();
                    logger.LogInformation("Database migration completed successfully");
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Database migration failed");
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

            app.MapGet("/version", () => Results.Ok(new
            {
                version = "0.2.0",
                timestamp = DateTime.UtcNow,
                environment = app.Environment.EnvironmentName
            }));

            app.Use(async (context, next) =>
            {
                var stopwatch = Stopwatch.StartNew();
                var method = context.Request.Method;
                var path = context.Request.Path.Value ?? "/";

                try
                {
                    await next();
                    stopwatch.Stop();
                    logger.LogInformation(
                        "HTTP {Method} {Path} responded {StatusCode} in {ElapsedMs} ms",
                        method,
                        path,
                        context.Response.StatusCode,
                        stopwatch.ElapsedMilliseconds);
                }
                catch (Exception ex)
                {
                    stopwatch.Stop();
                    logger.LogError(
                        ex,
                        "HTTP {Method} {Path} failed after {ElapsedMs} ms",
                        method,
                        path,
                        stopwatch.ElapsedMilliseconds);
                    throw;
                }
            });

            // Используем CORS в зависимости от окружения
            if (app.Environment.IsDevelopment())
            {
                app.UseCors("DevCors");
                logger.LogInformation("Using CORS policy: DevCors");
            }
            else
            {
                app.UseCors("ProdCors");
                logger.LogInformation("Using CORS policy: ProdCors");
            }

            app.UseAuthentication();
            app.UseAuthorization();
            app.MapControllers();

            logger.LogInformation("Application is running on port {Port}", port);
            logger.LogInformation("Environment: {EnvironmentName}", app.Environment.EnvironmentName);
            logger.LogInformation(
                "Auth email sender config: SMTP host={SmtpHost}, user={SmtpUser}, frontend={FrontendBaseUrl}",
                app.Configuration["Email:SmtpHost"],
                app.Configuration["Email:SmtpUser"],
                app.Configuration["Email:FrontendBaseUrl"]);

            app.Run();
        }
    }
}
