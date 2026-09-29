using System.Net;
using HockeyPlanner.Backend.Infrastructure.Data;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Testcontainers.PostgreSql;
using ApiProgram = HockeyPlanner.Backend.WebAPI.Program;

// Separate test executable, never referenced/published by WebAPI. No HTTP backdoors.
var readyFile = Environment.GetEnvironmentVariable("HP_E2E_READY_FILE")
    ?? throw new InvalidOperationException("Start via frontend npm run e2e.");
var signingKey = Environment.GetEnvironmentVariable("HP_E2E_SIGNING_KEY")
    ?? throw new InvalidOperationException("An ephemeral test signing key is required.");
await using var postgres = new PostgreSqlBuilder("postgres:16-alpine")
    .WithDatabase($"hockeyplanner_e2e_{Guid.NewGuid():N}")
    .WithUsername("postgres").WithPassword(Guid.NewGuid().ToString("N")).Build();
await postgres.StartAsync();
var connection = postgres.GetConnectionString();
Environment.SetEnvironmentVariable("ConnectionStrings__DefaultConnection", connection);
Environment.SetEnvironmentVariable("Jwt__SigningKey", signingKey);
Environment.SetEnvironmentVariable("Jwt__AccessTokenMinutes", "15");
Environment.SetEnvironmentVariable("ExternalLeagueSync__Enabled", "false");
Environment.SetEnvironmentVariable("NotificationWorker__Enabled", "false");
Environment.SetEnvironmentVariable("Logging__LogLevel__Default", "Warning");
await using (var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseNpgsql(connection).Options))
    await db.Database.MigrateAsync();
await using var factory = new BrowserApiFactory();
factory.UseKestrel(options => options.Listen(IPAddress.Loopback, 0));
using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
var address = factory.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
await File.WriteAllTextAsync(readyFile, System.Text.Json.JsonSerializer.Serialize(new { api = address }));
// Parent owns stdin. EOF (including parent crash) disposes Kestrel and the disposable container.
await Console.In.ReadLineAsync();

sealed class BrowserApiFactory : WebApplicationFactory<ApiProgram>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");
        builder.ConfigureTestServices(services =>
        {
            // No unattended network providers in browser tests. Durable enqueue/in-app flows remain real.
            foreach (var descriptor in services.Where(value => value.ServiceType == typeof(IHostedService)
                && value.ImplementationType?.Assembly == typeof(ApiProgram).Assembly).ToArray())
                services.Remove(descriptor);
        });
    }
}
