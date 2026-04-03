using HockeyPlanner.Backend.Application.Abstractions.Services;
using HockeyPlanner.Backend.Application.Implementations.Services;
using Microsoft.Extensions.DependencyInjection;

namespace HockeyPlanner.Backend.Application
{
    public static class DependencyInjection
    {
        public static IServiceCollection AddApplication(
            this IServiceCollection services)
        {
            services.AddScoped<IEventService, EventService>();
            services.AddScoped<IExerciseService, ExerciseService>();
            services.AddScoped<ILineService, LineService>();
            services.AddScoped<IPlayerService, PlayerService>();

            return services;
        }
    }

}
