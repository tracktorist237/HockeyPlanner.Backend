using HockeyPlanner.Backend.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.Processing;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace HockeyPlanner.Backend.WebAPI.Services
{
    public sealed class TeamPwaService : ITeamPwaService
    {
        private const int MaxLogoBytes = 5 * 1024 * 1024;
        private static readonly HashSet<int> SupportedIconSizes = [180, 192, 512];

        private readonly AppDbContext _context;
        private readonly IHttpClientFactory _httpClientFactory;
        private readonly IConfiguration _configuration;
        private readonly ILogger<TeamPwaService> _logger;

        public TeamPwaService(
            AppDbContext context,
            IHttpClientFactory httpClientFactory,
            IConfiguration configuration,
            ILogger<TeamPwaService> logger)
        {
            _context = context;
            _httpClientFactory = httpClientFactory;
            _configuration = configuration;
            _logger = logger;
        }

        public async Task<TeamPwaManifestResult?> GetManifestAsync(
            Guid teamId,
            string? requestedName,
            CancellationToken cancellationToken)
        {
            var team = await _context.Teams
                .AsNoTracking()
                .Where(value => value.Id == teamId)
                .Select(value => new { value.Name, value.AvatarUrl })
                .FirstOrDefaultAsync(cancellationToken);

            if (team == null || string.IsNullOrWhiteSpace(team.AvatarUrl))
            {
                return null;
            }

            var appName = NormalizeAppName(requestedName, team.Name);
            var origin = GetFrontendOrigin();
            var teamPath = $"/pwa/teams/{teamId:D}";
            var iconBase = $"{origin}/api/pwa/teams/{teamId:D}/icons";
            var manifest = new
            {
                id = teamPath,
                name = appName,
                short_name = TruncateText(appName, 24),
                description = $"Hockey Planner: {team.Name.Trim()}",
                start_url = teamPath,
                scope = "/",
                display = "standalone",
                theme_color = "#0b1220",
                background_color = "#ffffff",
                icons = new object[]
                {
                    new { src = $"{iconBase}/192.png", type = "image/png", sizes = "192x192", purpose = "any" },
                    new { src = $"{iconBase}/512.png", type = "image/png", sizes = "512x512", purpose = "any maskable" }
                }
            };

            var content = JsonSerializer.SerializeToUtf8Bytes(manifest);
            return new TeamPwaManifestResult(content, CreateEntityTag(content));
        }

        public async Task<TeamPwaIconResult?> GetIconAsync(
            Guid teamId,
            int size,
            CancellationToken cancellationToken)
        {
            if (!SupportedIconSizes.Contains(size))
            {
                return null;
            }

            var source = await GetOriginalLogoAsync(teamId, cancellationToken);
            if (source == null)
            {
                return null;
            }

            try
            {
                using var image = Image.Load(source.Content);
                var safeZoneSize = Math.Max(1, (int)Math.Floor(size * 0.76));
                image.Mutate(context => context
                    .Resize(new ResizeOptions
                    {
                        Size = new Size(safeZoneSize, safeZoneSize),
                        Mode = ResizeMode.Pad,
                        PadColor = Color.White,
                        Position = AnchorPositionMode.Center
                    })
                    .Pad(size, size, Color.White)
                    .BackgroundColor(Color.White));

                await using var output = new MemoryStream();
                await image.SaveAsync(output, new PngEncoder(), cancellationToken);
                var bytes = output.ToArray();
                return new TeamPwaIconResult(bytes, "image/png", CreateEntityTag(bytes));
            }
            catch (UnknownImageFormatException exception)
            {
                _logger.LogWarning(exception, "Unsupported PWA logo format for team {TeamId}", teamId);
                return null;
            }
        }

        public async Task<TeamPwaIconResult?> GetOriginalLogoAsync(
            Guid teamId,
            CancellationToken cancellationToken)
        {
            var avatarUrl = await _context.Teams
                .AsNoTracking()
                .Where(value => value.Id == teamId)
                .Select(value => value.AvatarUrl)
                .FirstOrDefaultAsync(cancellationToken);

            if (string.IsNullOrWhiteSpace(avatarUrl) ||
                !Uri.TryCreate(avatarUrl, UriKind.Absolute, out var avatarUri) ||
                avatarUri.Scheme != Uri.UriSchemeHttps ||
                !IsAllowedLogoSource(avatarUri))
            {
                return null;
            }

            try
            {
                var client = _httpClientFactory.CreateClient();
                using var response = await client.GetAsync(
                    avatarUri,
                    HttpCompletionOption.ResponseHeadersRead,
                    cancellationToken);

                if (!response.IsSuccessStatusCode || response.Content.Headers.ContentLength > MaxLogoBytes)
                {
                    return null;
                }

                await response.Content.LoadIntoBufferAsync(MaxLogoBytes, cancellationToken);
                var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken);
                var contentType = response.Content.Headers.ContentType?.MediaType;
                if (string.IsNullOrWhiteSpace(contentType) ||
                    !contentType.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
                {
                    return null;
                }

                return new TeamPwaIconResult(bytes, contentType, CreateEntityTag(bytes));
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception exception)
            {
                _logger.LogWarning(exception, "Failed to fetch PWA logo for team {TeamId}", teamId);
                return null;
            }
        }

        private string GetFrontendOrigin()
        {
            var configured = _configuration["Pwa:FrontendBaseUrl"] ?? _configuration["Email:FrontendBaseUrl"];
            if (!Uri.TryCreate(configured, UriKind.Absolute, out var uri))
            {
                throw new InvalidOperationException("Pwa:FrontendBaseUrl must be an absolute URL.");
            }

            return uri.GetLeftPart(UriPartial.Authority).TrimEnd('/');
        }

        private bool IsAllowedLogoSource(Uri avatarUri)
        {
            if (avatarUri.Host.Equals("ik.imagekit.io", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            var publicBaseUrl = _configuration["S3:PublicBaseUrl"];
            if (!Uri.TryCreate(publicBaseUrl, UriKind.Absolute, out var storageUri))
            {
                return false;
            }

            var storagePath = storageUri.AbsolutePath.TrimEnd('/');
            return avatarUri.Scheme.Equals(storageUri.Scheme, StringComparison.OrdinalIgnoreCase) &&
                   avatarUri.Host.Equals(storageUri.Host, StringComparison.OrdinalIgnoreCase) &&
                   avatarUri.Port == storageUri.Port &&
                   (string.IsNullOrEmpty(storagePath) ||
                    avatarUri.AbsolutePath.StartsWith($"{storagePath}/", StringComparison.Ordinal));
        }

        private static string NormalizeAppName(string? requestedName, string teamName)
        {
            var value = string.IsNullOrWhiteSpace(requestedName) ? teamName : requestedName;
            var normalized = string.Join(' ', value.Trim().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
            return TruncateText(string.IsNullOrWhiteSpace(normalized) ? "Hockey Planner" : normalized, 50);
        }

        private static string TruncateText(string value, int maxLength) =>
            value.Length <= maxLength ? value : value[..maxLength];

        private static string CreateEntityTag(byte[] content) =>
            $"\"{Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant()}\"";
    }
}
