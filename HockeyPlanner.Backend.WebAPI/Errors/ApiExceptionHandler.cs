using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;

namespace HockeyPlanner.Backend.WebAPI.Errors;

public sealed class ApiExceptionHandler(ILogger<ApiExceptionHandler> logger) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext context, Exception exception, CancellationToken cancellationToken)
    {
        if (context.Response.HasStarted) return false;

        // Exception messages/inner exceptions may contain credentials or upstream URLs.
        logger.LogError("Unhandled API error {ExceptionType}: {Method} {Path} returned {StatusCode}; TraceId {TraceId}",
            exception.GetType().FullName, context.Request.Method, context.Request.Path.Value,
            StatusCodes.Status500InternalServerError, context.TraceIdentifier);
        await ApiProblems.WriteAsync(context, StatusCodes.Status500InternalServerError, cancellationToken: cancellationToken);
        return true;
    }
}
