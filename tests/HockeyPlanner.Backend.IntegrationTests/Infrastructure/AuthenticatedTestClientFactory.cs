using System.Net.Http.Headers;
using HockeyPlanner.Backend.Core.Entities;
using HockeyPlanner.Backend.WebAPI.Services;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace HockeyPlanner.Backend.IntegrationTests.Infrastructure;

public static class AuthenticatedTestClientFactory
{
    public static HttpClient Create(HockeyPlannerWebApplicationFactory application, User user)
    {
        using var scope = application.Services.CreateScope();
        var tokenService = scope.ServiceProvider.GetRequiredService<IAuthTokenService>();
        var accessToken = tokenService.CreateAccessToken(user);

        var client = application.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
        });
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

        return client;
    }
}
