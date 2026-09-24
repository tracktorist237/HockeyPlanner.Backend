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
        context.Response.StatusCode = StatusCodes.Status500InternalServerError;
        var problem = new ProblemDetails
        {
            Status = StatusCodes.Status500InternalServerError,
            Title = "Внутренняя ошибка сервера",
            Detail = "Не удалось выполнить запрос. Попробуйте позже.",
            Type = "https://www.rfc-editor.org/rfc/rfc9110#section-15.6.1"
        };
        problem.Extensions["traceId"] = context.TraceIdentifier;
        await context.Response.WriteAsJsonAsync(problem, options: null, contentType: "application/problem+json", cancellationToken: cancellationToken);
        return true;
    }
}
