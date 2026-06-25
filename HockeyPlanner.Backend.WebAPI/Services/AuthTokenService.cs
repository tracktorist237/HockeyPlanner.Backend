using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using HockeyPlanner.Backend.Core.Entities;
using HockeyPlanner.Backend.WebAPI.Models.Auth;
using HockeyPlanner.Backend.WebAPI.Options;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

namespace HockeyPlanner.Backend.WebAPI.Services
{
    public sealed class AuthTokenService : IAuthTokenService
    {
        private readonly JwtOptions _options;
        private readonly MigrationOptions _migrationOptions;

        public AuthTokenService(IOptions<JwtOptions> options, IOptions<MigrationOptions> migrationOptions)
        {
            _options = options.Value;
            _migrationOptions = migrationOptions.Value;
        }

        public string CreateAccessToken(User user)
        {
            var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(_options.SigningKey));
            var credentials = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);
            var claims = new List<Claim>
            {
                new(JwtRegisteredClaimNames.Sub, user.Id.ToString()),
                new(ClaimTypes.NameIdentifier, user.Id.ToString()),
                new(ClaimTypes.Name, user.FullName),
                new(ClaimTypes.Email, user.Email ?? string.Empty),
                new(ClaimTypes.Role, user.AppRole.ToString()),
                new("app_role", user.AppRole.ToString()),
                new("app_role_id", ((int)user.AppRole).ToString()),
                new("hockey_role_id", ((int)user.Role).ToString())
            };

            var token = new JwtSecurityToken(
                issuer: _options.Issuer,
                audience: _options.Audience,
                claims: claims,
                expires: DateTime.UtcNow.AddMinutes(_options.AccessTokenMinutes),
                signingCredentials: credentials);

            return new JwtSecurityTokenHandler().WriteToken(token);
        }

        public string CreateMigrationToken(User user, DateTime expiresAt)
        {
            if (string.IsNullOrWhiteSpace(_migrationOptions.SigningKey))
            {
                throw new InvalidOperationException("Migration signing key is not configured.");
            }

            var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(_migrationOptions.SigningKey));
            var credentials = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);
            var issuedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString();
            var claims = new List<Claim>
            {
                new(JwtRegisteredClaimNames.Sub, user.Id.ToString()),
                new(ClaimTypes.NameIdentifier, user.Id.ToString()),
                new(ClaimTypes.Email, user.Email ?? string.Empty),
                new(JwtRegisteredClaimNames.Email, user.Email ?? string.Empty),
                new("purpose", "render-migration"),
                new(JwtRegisteredClaimNames.Iat, issuedAt, ClaimValueTypes.Integer64)
            };

            var token = new JwtSecurityToken(
                issuer: "HockeyPlanner.RenderMigration",
                audience: _migrationOptions.TargetFrontendUrl.TrimEnd('/'),
                claims: claims,
                expires: expiresAt,
                signingCredentials: credentials);

            return new JwtSecurityTokenHandler().WriteToken(token);
        }

        public MigrationTokenClaims ValidateMigrationToken(string token)
        {
            if (string.IsNullOrWhiteSpace(_migrationOptions.SigningKey))
            {
                throw new InvalidOperationException("Migration signing key is not configured.");
            }

            var principal = new JwtSecurityTokenHandler().ValidateToken(
                token,
                new TokenValidationParameters
                {
                    ValidateIssuer = true,
                    ValidIssuer = "HockeyPlanner.RenderMigration",
                    ValidateAudience = true,
                    ValidAudience = _migrationOptions.TargetFrontendUrl.TrimEnd('/'),
                    ValidateIssuerSigningKey = true,
                    IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(_migrationOptions.SigningKey)),
                    ValidateLifetime = true,
                    ClockSkew = TimeSpan.FromSeconds(30)
                },
                out _);

            var userIdValue = principal.FindFirstValue(ClaimTypes.NameIdentifier) ?? principal.FindFirstValue(JwtRegisteredClaimNames.Sub);
            if (!Guid.TryParse(userIdValue, out var userId))
            {
                throw new SecurityTokenValidationException("Migration token user id is invalid.");
            }

            return new MigrationTokenClaims
            {
                UserId = userId,
                Email = principal.FindFirstValue(ClaimTypes.Email) ?? principal.FindFirstValue(JwtRegisteredClaimNames.Email) ?? string.Empty,
                Purpose = principal.FindFirstValue("purpose") ?? string.Empty
            };
        }

        public string CreateOpaqueToken()
        {
            return Convert.ToBase64String(RandomNumberGenerator.GetBytes(64))
                .Replace("+", "-", StringComparison.Ordinal)
                .Replace("/", "_", StringComparison.Ordinal)
                .TrimEnd('=');
        }

        public string HashToken(string token)
        {
            var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(token));
            return Convert.ToHexString(bytes);
        }
    }
}
