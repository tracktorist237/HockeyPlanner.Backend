using Microsoft.AspNetCore.Diagnostics;
using HockeyPlanner.Backend.Application.Abstractions.Identity;
using HockeyPlanner.Backend.Core.Exceptions;
using Microsoft.EntityFrameworkCore;

namespace HockeyPlanner.Backend.WebAPI.Errors;

public sealed class ApiExceptionHandler(ILogger<ApiExceptionHandler> logger) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext context, Exception exception, CancellationToken cancellationToken)
    {
        if (context.Response.HasStarted) return false;

        var userId = context.RequestServices.GetService<ICurrentUser>()?.UserId;
        var status = exception switch
        {
            NotFoundException => 404,
            UnauthorizedException => userId.HasValue ? 403 : 401,
            BusinessRuleException or System.ComponentModel.DataAnnotations.ValidationException => 400,
            ConflictException or DbUpdateConcurrencyException => 409,
            HttpRequestException => 502,
            OperationCanceledException when !context.RequestAborted.IsCancellationRequested => 502,
            _ => 500
        };
        var detail = exception is NotFoundException or UnauthorizedException or BusinessRuleException or ConflictException
            ? exception.Message : null;

        // Exception messages/inner exceptions may contain credentials or upstream URLs.
        context.Items[ApiRequestContextMiddleware.ErrorLoggedKey] = true;
        logger.Log(status >= 500 ? LogLevel.Error : LogLevel.Warning,
            "API error {ExceptionType}: {Method} {Path} returned {StatusCode}; UserId {UserId}; TraceId {TraceId}",
            exception.GetType().FullName, context.Request.Method, ApiRequestContextMiddleware.SafePath(context),
            status, userId, context.TraceIdentifier);
        await ApiProblems.WriteAsync(context, status, detail, cancellationToken);
        return true;
    }
}
