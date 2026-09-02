using HockeyPlanner.Backend.Core.Entities;
using HockeyPlanner.Backend.Infrastructure.Data;
using HockeyPlanner.Backend.WebAPI;
using HockeyPlanner.Backend.WebAPI.Models.Spbhl;
using HockeyPlanner.Backend.WebAPI.Services;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Npgsql;
using Testcontainers.PostgreSql;

namespace HockeyPlanner.Backend.IntegrationTests.Infrastructure;

public sealed class HockeyPlannerWebApplicationFactory : WebApplicationFactory<Program>, IAsyncLifetime
{
    private static readonly SemaphoreSlim DefaultConnectionEnvironmentLock = new(1, 1);
    private const string DefaultConnectionEnvironmentVariable = "ConnectionStrings__DefaultConnection";
    private const int PostgreSqlPort = 5432;
    private const string TestDatabasePrefix = "hockeyplanner_test_";
    private readonly string _databaseName = $"{TestDatabasePrefix}{Guid.NewGuid():N}";
    private readonly PostgreSqlContainer _postgresContainer;
    private string? _containerConnectionString;
    private string? _originalDefaultConnection;
    private bool _defaultConnectionEnvironmentLockHeld;
    private bool _defaultConnectionOverridden;

    public HockeyPlannerWebApplicationFactory()
    {
        // Docker Engine 23 exposes API 1.42; newer engines remain backward-compatible with it.
        Environment.SetEnvironmentVariable("DOCKER_API_VERSION", "1.42");

        _postgresContainer = new PostgreSqlBuilder("postgres:16-alpine")
            .WithDatabase(_databaseName)
            .WithUsername("postgres")
            .WithPassword($"test_{Guid.NewGuid():N}")
            .Build();
    }

    public string DatabaseName => _databaseName;

    public ushort MappedPostgreSqlPort => _postgresContainer.GetMappedPublicPort(PostgreSqlPort);

    public HttpClient Client { get; private set; } = null!;

    public async ValueTask InitializeAsync()
    {
        await _postgresContainer.StartAsync();

        _containerConnectionString = _postgresContainer.GetConnectionString();
        ValidateContainerConnectionString(_containerConnectionString);

        await DefaultConnectionEnvironmentLock.WaitAsync();
        _defaultConnectionEnvironmentLockHeld = true;

        try
        {
            // Program reads this setting while registering services, before test-host configuration callbacks run.
            SetBootstrapConnectionString(_containerConnectionString);

            Client = CreateClient(new WebApplicationFactoryClientOptions
            {
                AllowAutoRedirect = false,
            });

            await using var scope = Services.CreateAsyncScope();
            var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();

            ValidateEffectiveConnectionString(dbContext.Database.GetDbConnection().ConnectionString);

            await dbContext.Database.EnsureCreatedAsync();

            if (!await dbContext.Database.CanConnectAsync())
            {
                throw new InvalidOperationException("The integration-test PostgreSQL database is not reachable.");
            }
        }
        catch
        {
            RestoreBootstrapConnectionString();
            throw;
        }
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        var connectionString = _containerConnectionString
            ?? throw new InvalidOperationException("PostgreSQL must be started before the test host is built.");

        // Development temporarily prevents Program.cs from running the production startup migration path.
        builder.UseEnvironment(Environments.Development);

        builder.ConfigureAppConfiguration((_, configuration) =>
        {
            configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:DefaultConnection"] = connectionString,
                ["Email:Provider"] = string.Empty,
                ["Storage:Provider"] = "ImageKit",
            });
        });

        builder.ConfigureTestServices(services =>
        {
            RemoveProductionDatabaseRegistrations(services);

            services.AddDbContext<AppDbContext>(options => options.UseNpgsql(connectionString));

            var databaseConfigurationCount = services.Count(descriptor =>
                descriptor.ServiceType == typeof(IDbContextOptionsConfiguration<AppDbContext>));

            if (databaseConfigurationCount != 1)
            {
                throw new InvalidOperationException(
                    $"Expected one AppDbContext configuration, found {databaseConfigurationCount}.");
            }

            RemoveBirthdayHostedService(services);
            ReplaceExternalServices(services);
        });
    }

    public override async ValueTask DisposeAsync()
    {
        try
        {
            Client?.Dispose();
            await base.DisposeAsync();
            await _postgresContainer.DisposeAsync();
        }
        finally
        {
            RestoreBootstrapConnectionString();
        }
    }

    private void SetBootstrapConnectionString(string connectionString)
    {
        _originalDefaultConnection = Environment.GetEnvironmentVariable(DefaultConnectionEnvironmentVariable);
        Environment.SetEnvironmentVariable(DefaultConnectionEnvironmentVariable, connectionString);
        _defaultConnectionOverridden = true;
    }

    private void RestoreBootstrapConnectionString()
    {
        if (!_defaultConnectionEnvironmentLockHeld)
        {
            return;
        }

        try
        {
            if (_defaultConnectionOverridden)
            {
                Environment.SetEnvironmentVariable(DefaultConnectionEnvironmentVariable, _originalDefaultConnection);
                _defaultConnectionOverridden = false;
            }
        }
        finally
        {
            _defaultConnectionEnvironmentLockHeld = false;
            DefaultConnectionEnvironmentLock.Release();
        }
    }

    private static void RemoveProductionDatabaseRegistrations(IServiceCollection services)
    {
        services.RemoveAll<AppDbContext>();
        services.RemoveAll<DbContextOptions>();
        services.RemoveAll<DbContextOptions<AppDbContext>>();
        services.RemoveAll<IDbContextFactory<AppDbContext>>();
        services.RemoveAll<IDbContextOptionsConfiguration<AppDbContext>>();
    }

    private static void RemoveBirthdayHostedService(IServiceCollection services)
    {
        var descriptors = services
            .Where(descriptor =>
                descriptor.ServiceType == typeof(IHostedService) &&
                descriptor.ImplementationType == typeof(BirthdayPushHostedService))
            .ToList();

        foreach (var descriptor in descriptors)
        {
            services.Remove(descriptor);
        }
    }

    private static void ReplaceExternalServices(IServiceCollection services)
    {
        services.RemoveAll<IAuthEmailSender>();
        services.RemoveAll<IWebPushService>();
        services.RemoveAll<IFileStorageService>();
        services.RemoveAll<IImageKitUploader>();
        services.RemoveAll<ISpbhlPlayerSearchService>();
        services.RemoveAll<ImageKitUploader>();

        services.AddSingleton<IAuthEmailSender>(NoOpAuthEmailSender.Instance);
        services.AddSingleton<IWebPushService>(NoOpWebPushService.Instance);
        services.AddSingleton<IFileStorageService>(NoOpFileStorageService.Instance);
        services.AddSingleton<IImageKitUploader>(NoOpImageKitUploader.Instance);
        services.AddSingleton<ISpbhlPlayerSearchService>(NoOpSpbhlPlayerSearchService.Instance);
    }

    private void ValidateContainerConnectionString(string connectionString)
    {
        var builder = new NpgsqlConnectionStringBuilder(connectionString);
        var mappedPort = MappedPostgreSqlPort;

        if (string.IsNullOrWhiteSpace(builder.Database) ||
            !builder.Database.StartsWith(TestDatabasePrefix, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("The test database name does not use the required safety prefix.");
        }

        if (!string.Equals(builder.Database, _databaseName, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("The container connection string targets an unexpected database.");
        }

        if (builder.Port != mappedPort)
        {
            throw new InvalidOperationException("The container connection string uses an unexpected PostgreSQL port.");
        }
    }

    private void ValidateEffectiveConnectionString(string connectionString)
    {
        var expected = new NpgsqlConnectionStringBuilder(
            _containerConnectionString
            ?? throw new InvalidOperationException("The container connection string is unavailable."));
        var actual = new NpgsqlConnectionStringBuilder(connectionString);

        if (string.IsNullOrWhiteSpace(actual.Database) ||
            !string.Equals(actual.Database, _databaseName, StringComparison.Ordinal) ||
            !actual.Database.StartsWith(TestDatabasePrefix, StringComparison.Ordinal) ||
            !string.Equals(actual.Host, expected.Host, StringComparison.OrdinalIgnoreCase) ||
            actual.Port != MappedPostgreSqlPort)
        {
            throw new InvalidOperationException("AppDbContext is not configured for the isolated test container.");
        }
    }

    private sealed class NoOpAuthEmailSender : IAuthEmailSender
    {
        public static readonly NoOpAuthEmailSender Instance = new();

        public Task SendEmailConfirmation(User user, string token, CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public Task SendPasswordReset(User user, string token, CancellationToken cancellationToken) =>
            Task.CompletedTask;
    }

    private sealed class NoOpWebPushService : IWebPushService
    {
        public static readonly NoOpWebPushService Instance = new();

        public bool IsConfigured => false;

        public Task<WebPushSendResult> SendAsync(
            PushSubscription subscription,
            object payload,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new WebPushSendResult { IsSuccess = true });
    }

    private sealed class NoOpFileStorageService : IFileStorageService
    {
        public static readonly NoOpFileStorageService Instance = new();

        public Task<FileStorageUploadResult> UploadAsync(
            FileStorageUploadRequest request,
            CancellationToken cancellationToken) =>
            Task.FromResult(new FileStorageUploadResult
            {
                PublicUrl = $"https://test.invalid/{request.FileName}",
                Key = $"test/{request.FileName}",
            });

        public Task DeleteAsync(string key, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class NoOpImageKitUploader : IImageKitUploader
    {
        public static readonly NoOpImageKitUploader Instance = new();

        public Task<string> UploadAsync(
            Stream stream,
            string fileName,
            string folder,
            CancellationToken cancellationToken) =>
            Task.FromResult($"https://test.invalid/{folder}/{fileName}");
    }

    private sealed class NoOpSpbhlPlayerSearchService : ISpbhlPlayerSearchService
    {
        public static readonly NoOpSpbhlPlayerSearchService Instance = new();

        public Task<SpbhlPlayersSearchResponse> SearchPlayers(
            string fullName,
            string? birthYear,
            int page,
            CancellationToken cancellationToken) =>
            Task.FromResult(new SpbhlPlayersSearchResponse
            {
                Page = page,
                TotalPages = 0,
                Players = [],
            });
    }
}
