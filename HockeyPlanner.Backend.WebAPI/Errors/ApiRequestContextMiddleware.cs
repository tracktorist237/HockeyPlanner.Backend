using HockeyPlanner.Backend.Application.Abstractions.Identity;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Routing;

namespace HockeyPlanner.Backend.WebAPI.Errors;

public sealed class ApiRequestContextMiddleware(RequestDelegate next, ILogger<ApiRequestContextMiddleware> logger)
{
    internal const string ErrorLoggedKey = "ApiErrorLogged";
    public async Task InvokeAsync(HttpContext context)
    {
        // Never trust client-supplied identifiers as log content.
        context.TraceIdentifier = Guid.NewGuid().ToString("N");
        context.Response.OnStarting(() =>
        {
            context.Response.Headers["X-Correlation-ID"] = context.TraceIdentifier;
            return Task.CompletedTask;
        });
        using var scope = logger.BeginScope(new Dictionary<string, object> { ["TraceId"] = context.TraceIdentifier });
        await next(context);
        if (context.Response.StatusCode < 400 || context.RequestAborted.IsCancellationRequested ||
            context.Items.ContainsKey(ErrorLoggedKey) || context.Request.Path is { Value: "/health" or "/api/health" }) return;

        logger.Log(context.Response.StatusCode >= 500 ? LogLevel.Error : LogLevel.Warning,
            "API response {Method} {Path} returned {StatusCode}; UserId {UserId}; TraceId {TraceId}",
            context.Request.Method, SafePath(context), context.Response.StatusCode,
            context.RequestServices.GetService<ICurrentUser>()?.UserId, context.TraceIdentifier);
    }

    internal static string SafePath(HttpContext context) =>
        ((context.GetEndpoint() ?? context.Features.Get<IExceptionHandlerFeature>()?.Endpoint) as RouteEndpoint)
        ?.RoutePattern.RawText ?? "[unmatched]";
}
