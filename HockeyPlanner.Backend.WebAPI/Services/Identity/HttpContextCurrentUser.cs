using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using HockeyPlanner.Backend.Application.Abstractions.Identity;

namespace HockeyPlanner.Backend.WebAPI.Services.Identity;

public sealed class HttpContextCurrentUser : ICurrentUser
{
    private readonly IHttpContextAccessor _httpContextAccessor;

    public HttpContextCurrentUser(IHttpContextAccessor httpContextAccessor)
    {
        _httpContextAccessor = httpContextAccessor;
    }

    public bool IsAuthenticated =>
        _httpContextAccessor.HttpContext?.User.Identity?.IsAuthenticated == true;

    public Guid? UserId
    {
        get
        {
            var principal = _httpContextAccessor.HttpContext?.User;
            if (principal?.Identity?.IsAuthenticated != true)
            {
                return null;
            }

            var subject = ResolveClaim(principal, JwtRegisteredClaimNames.Sub);
            var nameIdentifier = ResolveClaim(principal, ClaimTypes.NameIdentifier);

            if (subject.IsInvalid || nameIdentifier.IsInvalid)
            {
                return null;
            }

            if (subject.Value.HasValue)
            {
                return nameIdentifier.Value.HasValue && nameIdentifier.Value != subject.Value
                    ? null
                    : subject.Value;
            }

            return nameIdentifier.Value;
        }
    }

    private static ClaimResolution ResolveClaim(ClaimsPrincipal principal, string claimType)
    {
        var values = principal.FindAll(claimType)
            .Select(claim => claim.Value)
            .Distinct(StringComparer.Ordinal)
            .ToList();

        if (values.Count == 0)
        {
            return ClaimResolution.Missing;
        }

        if (values.Count != 1 || !Guid.TryParse(values[0], out var userId))
        {
            return ClaimResolution.Invalid;
        }

        return new ClaimResolution(userId, false);
    }

    private readonly record struct ClaimResolution(Guid? Value, bool IsInvalid)
    {
        public static ClaimResolution Missing { get; } = new(null, false);
        public static ClaimResolution Invalid { get; } = new(null, true);
    }
}
