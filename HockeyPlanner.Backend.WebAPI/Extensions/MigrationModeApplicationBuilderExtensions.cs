using HockeyPlanner.Backend.WebAPI.Options;
using Microsoft.Extensions.Options;

namespace HockeyPlanner.Backend.WebAPI.Extensions
{
    public static class MigrationModeApplicationBuilderExtensions
    {
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
                    !context.Request.Path.StartsWithSegments("/api"))
                {
                    await next();
                    return;
                }

                context.Response.StatusCode = StatusCodes.Status410Gone;
                await context.Response.WriteAsJsonAsync(new
                {
                    message = "Hockey Planner переехал на новый адрес. Старое приложение больше не работает.",
                    newUrl = options.TargetFrontendUrl.TrimEnd('/')
                });
            });
        }
    }
}
