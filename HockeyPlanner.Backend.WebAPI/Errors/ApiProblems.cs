using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace HockeyPlanner.Backend.WebAPI.Errors;

public static class ApiProblems
{
    public static ProblemDetails Create(HttpContext context, int status, string? detail = null,
        IDictionary<string, string[]>? errors = null)
    {
        var title = status switch
        {
            400 => "Некорректный запрос",
            401 => "Необходима авторизация",
            403 => "Недостаточно прав",
            404 => "Не найдено",
            409 => "Конфликт данных",
            422 => "Ошибка проверки данных",
            429 => "Слишком много запросов",
            502 or 503 or 504 => "Сервис временно недоступен",
            _ => status >= 500 ? "Внутренняя ошибка сервера" : "Не удалось выполнить запрос"
        };
        // Server failures must never echo exception text or legacy internal payloads.
        var safeDetail = status >= 500
            ? "Не удалось выполнить запрос. Попробуйте позже."
            : string.IsNullOrWhiteSpace(detail) ? title : detail;
        ProblemDetails problem = errors is null ? new ProblemDetails() : new ValidationProblemDetails(errors);
        problem.Status = status;
        problem.Type = "about:blank";
        problem.Title = title;
        problem.Detail = safeDetail;
        problem.Extensions["traceId"] = context.TraceIdentifier;
        // Transitional aliases keep existing clients compatible while they adopt ProblemDetails.
        problem.Extensions["message"] = safeDetail;
        problem.Extensions["error"] = safeDetail;
        return problem;
    }

    public static ObjectResult Result(ProblemDetails problem)
    {
        var result = new ObjectResult(problem) { StatusCode = problem.Status };
        result.ContentTypes.Add("application/problem+json");
        return result;
    }

    public static Task WriteAsync(HttpContext context, int status, string? detail = null, CancellationToken cancellationToken = default)
    {
        context.Response.StatusCode = status;
        return context.Response.WriteAsJsonAsync(Create(context, status, detail), options: null,
            contentType: "application/problem+json", cancellationToken: cancellationToken);
    }
}

// Normalize explicit controller errors without buffering successful or streaming responses.
public sealed class ApiProblemResultFilter : IAlwaysRunResultFilter, IOrderedFilter
{
    public int Order => int.MaxValue;
    public void OnResultExecuting(ResultExecutingContext context)
    {
        if (context.Result is not ObjectResult result || result.StatusCode is not >= 400) return;
        var status = result.StatusCode.Value;
        string? detail = null;
        JsonElement? conflicts = null;
        IDictionary<string, string[]>? errors = null;
        if (result.Value is ProblemDetails problem)
        {
            detail = problem.Detail ?? problem.Title;
            if (problem is ValidationProblemDetails validation) errors = validation.Errors;
        }
        else if (status < 500 && result.Value is not null)
        {
            var payload = JsonSerializer.SerializeToElement(result.Value);
            if (payload.ValueKind == JsonValueKind.String) detail = payload.GetString();
            else if (payload.ValueKind == JsonValueKind.Object)
            {
                // The attendance confirmation UX consumes this existing structured 409 payload.
                if (status == 409 && payload.TryGetProperty("conflicts", out var values)) conflicts = values;
                foreach (var key in new[] { "detail", "message", "error", "title" })
                    if (payload.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String)
                    {
                        detail = value.GetString();
                        break;
                    }
            }
        }
        var normalized = ApiProblems.Create(context.HttpContext, status, detail, errors);
        if (conflicts.HasValue) normalized.Extensions["conflicts"] = conflicts.Value;
        context.Result = ApiProblems.Result(normalized);
    }
    public void OnResultExecuted(ResultExecutedContext context) { }
}
