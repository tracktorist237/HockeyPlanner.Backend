using System.Net;
using System.Text.Json;
using HockeyPlanner.Backend.WebAPI.Errors;
using Microsoft.AspNetCore.TestHost;
using Microsoft.AspNetCore.Mvc;
using System.ComponentModel.DataAnnotations;
using System.Net.Http.Json;
using HockeyPlanner.Backend.Core.Exceptions;
using HockeyPlanner.Backend.Application.Abstractions.Identity;

namespace HockeyPlanner.Backend.IntegrationTests.Errors;

public sealed class ApiErrorPipelineTests
{
    [Theory]
    [InlineData("not-found", false, 404)]
    [InlineData("business", true, 400)]
    [InlineData("access", false, 401)]
    [InlineData("access", true, 403)]
    [InlineData("conflict", true, 409)]
    [InlineData("upstream", true, 502)]
    [InlineData("timeout", true, 502)]
    public async Task TypedFailures_PreserveHttpSemantics(string kind, bool authenticated, int status)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddExceptionHandler<ApiExceptionHandler>();
        builder.Services.AddProblemDetails();
        builder.Services.AddSingleton<ICurrentUser>(new TestCurrentUser(authenticated));
        await using var app = builder.Build();
        app.UseExceptionHandler(new ExceptionHandlerOptions { SuppressDiagnosticsCallback = _ => true });
        Exception failure = kind switch
        {
            "not-found" => new NotFoundException("Ресурс не найден"),
            "business" => new BusinessRuleException("Некорректные данные переноса"),
            "access" => new UnauthorizedException("Нет доступа"),
            "conflict" => new ConflictException("Данные уже изменены"),
            "upstream" => new HttpRequestException("token=SECRET"),
            _ => new TaskCanceledException("token=SECRET")
        };
        app.MapGet("/failure", (HttpContext _) => Task.FromException(failure));
        await app.StartAsync(TestContext.Current.CancellationToken);
        var response = await app.GetTestClient().GetAsync("/failure", TestContext.Current.CancellationToken);
        Assert.Equal(status, (int)response.StatusCode);
        Assert.DoesNotContain("SECRET", await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
    }

    private sealed class TestCurrentUser(bool authenticated) : ICurrentUser
    {
        public bool IsAuthenticated => authenticated;
        public Guid? UserId => authenticated ? Guid.Parse("00000000-0000-0000-0000-000000000001") : null;
    }
    [Theory]
    [InlineData(400)]
    [InlineData(401)]
    [InlineData(403)]
    [InlineData(404)]
    [InlineData(409)]
    [InlineData(500)]
    [InlineData(502)]
    public async Task ExplicitAndEmptyErrors_HaveSameContract(int status)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddControllers(options => options.Filters.Add<ApiProblemResultFilter>())
            .AddApplicationPart(typeof(ErrorProbeController).Assembly);
        await using var app = builder.Build();
        app.UseStatusCodePages(context => ApiProblems.WriteAsync(context.HttpContext, context.HttpContext.Response.StatusCode));
        app.MapControllers();
        app.MapGet("/empty/{status:int}", (int status) => Results.StatusCode(status));
        await app.StartAsync(TestContext.Current.CancellationToken);
        using var client = app.GetTestClient();
        foreach (var url in new[] { $"/error-probe/{status}", $"/empty/{status}" })
        {
            var response = await client.GetAsync(url, TestContext.Current.CancellationToken);
            var json = await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
            Assert.Equal(status, (int)response.StatusCode);
            Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
            Assert.Equal(status, json.GetProperty("status").GetInt32());
            Assert.Equal("about:blank", json.GetProperty("type").GetString());
            Assert.False(string.IsNullOrWhiteSpace(json.GetProperty("title").GetString()));
            Assert.Equal(json.GetProperty("detail").GetString(), json.GetProperty("message").GetString());
            Assert.True(json.TryGetProperty("traceId", out _));
            if (status >= 500) Assert.DoesNotContain("private payload", json.ToString());
        }
    }

    [Fact]
    public async Task Validation_PreservesFieldErrors()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddControllers(options => options.Filters.Add<ApiProblemResultFilter>())
            .AddApplicationPart(typeof(ErrorProbeController).Assembly);
        await using var app = builder.Build();
        app.MapControllers();
        await app.StartAsync(TestContext.Current.CancellationToken);
        var response = await app.GetTestClient().PostAsJsonAsync("/error-probe/validation", new { }, TestContext.Current.CancellationToken);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.True(json.GetProperty("errors").TryGetProperty("Name", out _));
        Assert.True(json.TryGetProperty("traceId", out _));
        var conflictResponse = await app.GetTestClient().GetAsync("/error-probe/attendance-conflict", TestContext.Current.CancellationToken);
        var conflict = await conflictResponse.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Conflict, conflictResponse.StatusCode);
        Assert.Equal("event-1", conflict.GetProperty("conflicts")[0].GetProperty("id").GetString());
    }
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

[ApiController]
[Route("error-probe")]
public sealed class ErrorProbeController : ControllerBase
{
    [HttpGet("attendance-conflict")]
    public IActionResult AttendanceConflict() => Conflict(new { message = "В это время у вас уже есть мероприятие", conflicts = new[] { new { id = "event-1", title = "Матч" } } });

    [HttpGet("{status:int}")]
    public IActionResult Error(int status) => StatusCode(status, new { message = "private payload" });

    [HttpPost("validation")]
    public IActionResult Validate(ErrorProbeRequest request) => Ok();
}

public sealed class ErrorProbeRequest
{
    [Required] public string? Name { get; set; }
}
