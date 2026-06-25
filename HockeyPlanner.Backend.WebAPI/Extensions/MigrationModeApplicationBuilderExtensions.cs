using HockeyPlanner.Backend.WebAPI.Options;
using Microsoft.Extensions.Options;

namespace HockeyPlanner.Backend.WebAPI.Extensions
{
    public static class MigrationModeApplicationBuilderExtensions
    {
        private static readonly HashSet<(string Method, string Path)> AllowedApiEndpoints = new()
        {
            ("POST", "/api/auth/login"),
            ("POST", "/api/auth/refresh"),
            ("POST", "/api/auth/migration-token")
        };

        public static IApplicationBuilder UseRenderMigrationModeBlocker(this IApplicationBuilder app)
        {
            return app.Use(async (context, next) =>
            {
                var options = context.RequestServices.GetRequiredService<IOptions<MigrationOptions>>().Value;
                if (!options.RenderMigrationMode)
                {
                    await next();
                    return;
                }

                if (HttpMethods.IsOptions(context.Request.Method) ||
                    !context.Request.Path.StartsWithSegments("/api") ||
                    IsAllowedApiEndpoint(context.Request.Method, context.Request.Path))
                {
                    await next();
                    return;
                }

                context.Response.StatusCode = StatusCodes.Status410Gone;
                await context.Response.WriteAsJsonAsync(new
                {
                    message = "Hockey Planner переехал на новый адрес.",
                    newUrl = options.TargetFrontendUrl.TrimEnd('/')
                });
            });
        }

        private static bool IsAllowedApiEndpoint(string method, PathString path)
        {
            var normalizedPath = path.Value?.TrimEnd('/') ?? string.Empty;
            if (string.IsNullOrWhiteSpace(normalizedPath))
            {
                normalizedPath = "/";
            }

            return AllowedApiEndpoints.Contains((method.ToUpperInvariant(), normalizedPath.ToLowerInvariant()));
        }
    }
}
