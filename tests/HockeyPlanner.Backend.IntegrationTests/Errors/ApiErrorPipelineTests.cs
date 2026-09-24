using System.Net;
using System.Text.Json;
using HockeyPlanner.Backend.WebAPI.Errors;
using Microsoft.AspNetCore.TestHost;

namespace HockeyPlanner.Backend.IntegrationTests.Errors;

public sealed class ApiErrorPipelineTests
{
    [Fact]
    public async Task UnexpectedException_ReturnsSafeProductionProblem_AndLogsOnce()
    {
        var logs = new CapturedErrorLogs();
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Production" });
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        builder.Logging.AddProvider(logs);
        builder.Services.AddExceptionHandler<ApiExceptionHandler>();
        builder.Services.AddProblemDetails();
        await using var app = builder.Build();
        app.UseExceptionHandler(new ExceptionHandlerOptions { SuppressDiagnosticsCallback = _ => true });
        app.MapGet("/failure", (HttpContext _) => Task.FromException(new InvalidOperationException("password=SECRET refresh_token=SECRET")));
        await app.StartAsync(TestContext.Current.CancellationToken);
        using var client = app.GetTestClient();

        var response = await client.GetAsync("/failure", TestContext.Current.CancellationToken);
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        var problem = JsonDocument.Parse(body).RootElement;

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal(500, problem.GetProperty("status").GetInt32());
        Assert.False(string.IsNullOrWhiteSpace(problem.GetProperty("traceId").GetString()));
        Assert.DoesNotContain("SECRET", body);
        Assert.DoesNotContain(nameof(InvalidOperationException), body);
        Assert.Single(logs.Errors);
        Assert.DoesNotContain("SECRET", logs.Errors[0]);
    }

    private sealed class CapturedErrorLogs : ILoggerProvider
    {
        public List<string> Errors { get; } = [];
        public ILogger CreateLogger(string categoryName) => new CaptureLogger(Errors);
        public void Dispose() { }
        private sealed class CaptureLogger(List<string> errors) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel logLevel) => true;
            public void Log<TState>(LogLevel level, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            {
                if (level >= LogLevel.Error) errors.Add(formatter(state, exception));
            }
        }
    }
}
