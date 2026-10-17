using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using HockeyPlanner.Backend.Infrastructure.Data;
using HockeyPlanner.Backend.IntegrationTests.Fixtures;
using HockeyPlanner.Backend.IntegrationTests.Infrastructure;
using HockeyPlanner.Backend.WebAPI.Services;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats;
using SixLabors.ImageSharp.Formats.Bmp;
using SixLabors.ImageSharp.Formats.Gif;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.Formats.Tiff;
using SixLabors.ImageSharp.Formats.Webp;
using SixLabors.ImageSharp.PixelFormats;

namespace HockeyPlanner.Backend.IntegrationTests.Services;

[Collection(IntegrationTestCollection.Name)]
public sealed class TeamPwaImageTests(HockeyPlannerWebApplicationFactory factory)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private const string SourceUrl = "https://ik.imagekit.io/pwa-tests/logo";
    private static readonly byte[] PngSignature = [137, 80, 78, 71, 13, 10, 26, 10];

    [Theory]
    [InlineData("png", 180, 136)]
    [InlineData("png", 192, 145)]
    [InlineData("png", 512, 389)]
    [InlineData("jpeg", 192, 145)]
    [InlineData("gif", 192, 145)]
    [InlineData("bmp", 192, 145)]
    [InlineData("tiff", 192, 145)]
    [InlineData("webp", 192, 145)]
    public async Task IconHttp_ProducesCenteredPngAndStableContentEtag(string format, int size, int expectedWidth)
    {
        var teamId = await Seed(SourceUrl);
        var bytes = Encode(format);
        using var upstream = new LogoHandler((_, _) => Task.FromResult(Response(bytes, "image/" + format)));
        using var host = Host(upstream);
        using var client = host.CreateClient();
        using var response = await client.GetAsync($"/api/pwa/teams/{teamId}/icons/{size}.png", Ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("image/png", response.Content.Headers.ContentType?.MediaType);
        AssertCache(response, 3600);
        var output = await response.Content.ReadAsByteArrayAsync(Ct);
        Assert.Equal(PngSignature, output[..8]);
        Assert.Equal(EntityTag(output), response.Headers.ETag?.Tag);
        using var image = Image.Load<Rgba32>(output);
        Assert.Equal(size, image.Width);
        Assert.Equal(size, image.Height);
        Assert.Equal(new Rgba32(255, 255, 255), image[0, 0]);
        Assert.Equal(new Rgba32(255, 255, 255), image[size - 1, size - 1]);
        var red = image[size / 2, size / 2];
        Assert.InRange(red.R, (byte)250, (byte)255);
        Assert.InRange(red.G, (byte)0, (byte)5);
        Assert.InRange(red.B, (byte)0, (byte)5);
        Assert.Equal((byte)255, red.A);
        // Measure actual pixels: the rectangular logo must retain its aspect ratio,
        // occupy the safe zone horizontally, and have balanced white margins.
        var colored = new List<(int X, int Y)>();
        for (var y = 0; y < size; y++)
            for (var x = 0; x < size; x++)
                if (image[x, y].G < 128 && image[x, y].B < 128)
                    colored.Add((x, y));
        Assert.NotEmpty(colored);
        var minX = colored.Min(p => p.X);
        var maxX = colored.Max(p => p.X);
        var minY = colored.Min(p => p.Y);
        var maxY = colored.Max(p => p.Y);
        Assert.Equal(expectedWidth, maxX - minX + 1);
        Assert.InRange(maxY - minY + 1, expectedWidth / 2, expectedWidth / 2 + 1);
        Assert.InRange(Math.Abs(minX - (size - 1 - maxX)), 0, 1);
        Assert.InRange(Math.Abs(minY - (size - 1 - maxY)), 0, 2);
        using var repeat = await client.GetAsync($"/api/pwa/teams/{teamId}/icons/{size}.png", Ct);
        Assert.Equal(HttpStatusCode.OK, repeat.StatusCode);
        Assert.Equal(output, await repeat.Content.ReadAsByteArrayAsync(Ct));
        Assert.Equal(response.Headers.ETag, repeat.Headers.ETag);
        Assert.Equal(2, upstream.Requests.Count);
        Assert.All(upstream.Requests, uri => Assert.Equal(SourceUrl, uri.ToString()));
    }

    [Theory]
    [InlineData(180)]
    [InlineData(192)]
    [InlineData(512)]
    public async Task TransparentPng_IsCompositedOntoOpaqueWhite(int size)
    {
        var teamId = await Seed(SourceUrl);
        using var input = new Image<Rgba32>(40, 20, new Rgba32(255, 0, 0, 128));
        using var buffer = new MemoryStream();
        input.Save(buffer, new PngEncoder());
        using var upstream = new LogoHandler((_, _) => Task.FromResult(Response(buffer.ToArray())));
        using var host = Host(upstream);
        await using var scope = host.Services.CreateAsyncScope();
        var icon = await scope.ServiceProvider.GetRequiredService<ITeamPwaService>().GetIconAsync(teamId, size, Ct);
        Assert.NotNull(icon);
        using var image = Image.Load<Rgba32>(icon.Content);
        var pixel = image[size / 2, size / 2];
        Assert.Equal((byte)255, pixel.A);
        Assert.Equal((byte)255, pixel.R);
        Assert.InRange(pixel.G, (byte)126, (byte)128);
        Assert.InRange(pixel.B, (byte)126, (byte)128);
        Assert.Equal(new Rgba32(255, 255, 255), image[0, 0]);
        Assert.Single(upstream.Requests);
    }

    [Theory]
    [InlineData("unsupported")]
    [InlineData("truncated")]
    [InlineData("empty")]
    public async Task BadImageHttp_ReturnsExisting404WithoutDecoderDetails(string kind)
    {
        var input = kind switch
        {
            "unsupported" => "not an image: private source detail"u8.ToArray(),
            "truncated" => Encode("png")[..33], // Real PNG signature and IHDR, but no image data.
            _ => []
        };
        var teamId = await Seed(SourceUrl);
        using var upstream = new LogoHandler((_, _) => Task.FromResult(Response(input)));
        using var host = Host(upstream);
        using var client = host.CreateClient();
        using var response = await client.GetAsync($"/api/pwa/teams/{teamId}/icons/192.png", Ct);
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        var text = await response.Content.ReadAsStringAsync(Ct);
        using var json = JsonDocument.Parse(text);
        Assert.Equal("Команда, логотип или размер иконки не найдены.", json.RootElement.GetProperty("message").GetString());
        Assert.Equal(new[] { "detail", "error", "message", "status", "title", "traceId", "type" },
            json.RootElement.EnumerateObject().Select(p => p.Name).Order().ToArray());
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal(404, json.RootElement.GetProperty("status").GetInt32());
        Assert.Equal("about:blank", json.RootElement.GetProperty("type").GetString());
        Assert.Equal("Не найдено", json.RootElement.GetProperty("title").GetString());
        Assert.False(string.IsNullOrWhiteSpace(json.RootElement.GetProperty("traceId").GetString()));
        foreach (var alias in new[] { "detail", "error" })
            Assert.Equal(json.RootElement.GetProperty("message").GetString(), json.RootElement.GetProperty(alias).GetString());
        Assert.DoesNotContain("Exception", text);
        Assert.DoesNotContain("private source detail", text);
        Assert.Null(response.Headers.ETag);
        Assert.Single(upstream.Requests);
    }

    [Theory]
    [InlineData("status")]
    [InlineData("network")]
    [InlineData("mime")]
    [InlineData("size-header")]
    [InlineData("size-buffer")]
    public async Task SourceFailures_AreSafe404AndDoNotRetry(string failure)
    {
        var teamId = await Seed(SourceUrl);
        using var upstream = new LogoHandler((_, _) =>
        {
            if (failure == "network") throw new HttpRequestException("private upstream detail");
            var response = Response(failure == "size-buffer" ? new byte[5 * 1024 * 1024 + 1] : Encode("png"));
            if (failure == "status") response.StatusCode = HttpStatusCode.BadGateway;
            if (failure == "mime") response.Content.Headers.ContentType = new MediaTypeHeaderValue("text/plain");
            if (failure == "size-header") response.Content.Headers.ContentLength = 5 * 1024 * 1024 + 1;
            // Buffer-limit path must run even when the upstream length is not known.
            if (failure == "size-buffer") response.Content = new UnknownLengthContent(new byte[5 * 1024 * 1024 + 1]);
            return Task.FromResult(response);
        });
        using var host = Host(upstream);
        using var client = host.CreateClient();
        using var response = await client.GetAsync($"/api/pwa/teams/{teamId}/icons/192.png", Ct);
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.DoesNotContain("private upstream detail", await response.Content.ReadAsStringAsync(Ct));
        Assert.Single(upstream.Requests);
    }

    [Theory]
    [InlineData("http://ik.imagekit.io/logo")]
    [InlineData("https://untrusted.test.invalid/logo")]
    [InlineData("https://ik.imagekit.io.attacker.test.invalid/logo")]
    [InlineData("")]
    public async Task DisallowedSource_MakesNoNetworkRequest(string url)
    {
        var teamId = await Seed(url);
        using var upstream = new LogoHandler((_, _) => throw new InvalidOperationException("Unexpected network request"));
        using var host = Host(upstream);
        using var client = host.CreateClient();
        using var response = await client.GetAsync($"/api/pwa/teams/{teamId}/icons/192.png", Ct);
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Empty(upstream.Requests);
    }

    [Fact]
    public async Task UnsupportedSizeAndMissingTeam_MakeNoNetworkRequest()
    {
        var teamId = await Seed(SourceUrl);
        using var upstream = new LogoHandler((_, _) => throw new InvalidOperationException("Unexpected network request"));
        using var host = Host(upstream);
        using var client = host.CreateClient();
        using var size = await client.GetAsync($"/api/pwa/teams/{teamId}/icons/256.png", Ct);
        using var missing = await client.GetAsync($"/api/pwa/teams/{Guid.NewGuid()}/icons/192.png", Ct);
        Assert.Equal(HttpStatusCode.NotFound, size.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        Assert.Empty(upstream.Requests);
    }

    [Theory]
    [InlineData("https://storage.test.invalid/logos/team.png", true)]
    [InlineData("https://storage.test.invalid/logos-other/team.png", false)]
    [InlineData("https://storage.test.invalid:444/logos/team.png", false)]
    public async Task ConfiguredStorage_RequiresMatchingOriginAndPathBoundary(string url, bool allowed)
    {
        var teamId = await Seed(url);
        using var upstream = new LogoHandler((_, _) => Task.FromResult(Response(Encode("png"))));
        using var host = Host(upstream, "https://storage.test.invalid/logos");
        using var client = host.CreateClient();
        using var response = await client.GetAsync($"/api/pwa/teams/{teamId}/icons/192.png", Ct);
        Assert.Equal(allowed ? HttpStatusCode.OK : HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal(allowed ? 1 : 0, upstream.Requests.Count);
        Assert.All(upstream.Requests, uri => Assert.Equal(url, uri.ToString()));
    }

    [Fact]
    public async Task CancellationDuringFetch_PropagatesAndDoesNotReturnNullOrRetry()
    {
        var teamId = await Seed(SourceUrl);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        using var upstream = new LogoHandler((_, token) =>
        {
            cancellation.Cancel();
            token.ThrowIfCancellationRequested();
            throw new InvalidOperationException("Cancellation was not forwarded");
        });
        using var host = Host(upstream);
        await using var scope = host.Services.CreateAsyncScope();
        var service = scope.ServiceProvider.GetRequiredService<ITeamPwaService>();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.GetIconAsync(teamId, 192, cancellation.Token));
        Assert.Single(upstream.Requests);
    }

    [Fact]
    public async Task AlreadyCancelledRequest_DoesNotReachLogoSource()
    {
        var teamId = await Seed(SourceUrl);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        using var upstream = new LogoHandler((_, _) => throw new InvalidOperationException("Unexpected network request"));
        using var host = Host(upstream);
        await using var scope = host.Services.CreateAsyncScope();
        var service = scope.ServiceProvider.GetRequiredService<ITeamPwaService>();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.GetIconAsync(teamId, 192, cancellation.Token));
        Assert.Empty(upstream.Requests);
    }

    [Fact]
    public async Task OriginalLogoAndManifestHttp_KeepSerializedContractsAndEtags()
    {
        var teamId = await Seed(SourceUrl);
        var original = Encode("jpeg");
        using var upstream = new LogoHandler((_, _) => Task.FromResult(Response(original, "image/jpeg")));
        using var host = Host(upstream);
        using var client = host.CreateClient();
        using var logo = await client.GetAsync($"/api/teams/{teamId}/pwa-logo", Ct);
        Assert.Equal(HttpStatusCode.OK, logo.StatusCode);
        Assert.Equal("image/jpeg", logo.Content.Headers.ContentType?.MediaType);
        Assert.Equal(original, await logo.Content.ReadAsByteArrayAsync(Ct));
        Assert.Equal(EntityTag(original), logo.Headers.ETag?.Tag);
        AssertCache(logo, 3600);
        using var manifest = await client.GetAsync($"/api/pwa/teams/{teamId}/manifest.webmanifest?name=Test%20Club", Ct);
        Assert.Equal(HttpStatusCode.OK, manifest.StatusCode);
        Assert.Equal("application/manifest+json", manifest.Content.Headers.ContentType?.MediaType);
        AssertCache(manifest, 300);
        var content = await manifest.Content.ReadAsByteArrayAsync(Ct);
        Assert.Equal(EntityTag(content), manifest.Headers.ETag?.Tag);
        using var json = JsonDocument.Parse(content);
        Assert.Equal("Test Club", json.RootElement.GetProperty("name").GetString());
        Assert.Equal($"/pwa/teams/{teamId:D}", json.RootElement.GetProperty("start_url").GetString());
        var icons = json.RootElement.GetProperty("icons").EnumerateArray().ToArray();
        Assert.Equal(2, icons.Length);
        Assert.Equal("192x192", icons[0].GetProperty("sizes").GetString());
        Assert.Equal("512x512", icons[1].GetProperty("sizes").GetString());
        Assert.Equal("any maskable", icons[1].GetProperty("purpose").GetString());
        Assert.All(icons, icon => Assert.Equal("image/png", icon.GetProperty("type").GetString()));
        Assert.Single(upstream.Requests); // The manifest never fetches the external source.
    }

    private async Task<Guid> Seed(string url)
    {
        var scenario = await TwoTeamSecurityScenarioBuilder.CreateAsync(factory.Services, Ct);
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var team = await db.Teams.FindAsync([scenario.TeamA.Id], Ct);
        Assert.NotNull(team);
        team.AvatarUrl = url;
        await db.SaveChangesAsync(Ct);
        return team.Id;
    }

    private WebApplicationFactory<HockeyPlanner.Backend.WebAPI.Program> Host(LogoHandler handler, string? storageBase = null) =>
        factory.WithWebHostBuilder(builder =>
        {
            if (storageBase is not null)
                builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(
                    new Dictionary<string, string?> { ["S3:PublicBaseUrl"] = storageBase }));
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<IHttpClientFactory>();
                services.AddSingleton<IHttpClientFactory>(new LogoClientFactory(handler));
            });
        });

    private static byte[] Encode(string format)
    {
        IImageEncoder encoder = format switch
        {
            "jpeg" => new JpegEncoder { Quality = 100 },
            "gif" => new GifEncoder(),
            "bmp" => new BmpEncoder(),
            "tiff" => new TiffEncoder(),
            "webp" => new WebpEncoder { FileFormat = WebpFileFormatType.Lossless },
            _ => new PngEncoder()
        };
        using var image = new Image<Rgba32>(40, 20, new Rgba32(255, 0, 0));
        using var stream = new MemoryStream();
        image.Save(stream, encoder);
        return stream.ToArray();
    }

    private static string EntityTag(byte[] bytes) => $"\"{Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant()}\"";

    private static void AssertCache(HttpResponseMessage response, int seconds)
    {
        var cache = response.Headers.CacheControl;
        Assert.NotNull(cache);
        Assert.True(cache.Public);
        Assert.True(cache.MustRevalidate);
        Assert.Equal(TimeSpan.FromSeconds(seconds), cache.MaxAge);
        Assert.False(cache.Private);
        Assert.False(cache.NoStore);
    }

    private static HttpResponseMessage Response(byte[] bytes, string mime = "image/png")
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) };
        response.Content.Headers.ContentType = new MediaTypeHeaderValue(mime);
        return response;
    }

    private sealed class LogoClientFactory(LogoHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private sealed class LogoHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        public List<Uri> Requests { get; } = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request.RequestUri!);
            return send(request, cancellationToken);
        }
    }

    private sealed class UnknownLengthContent(byte[] bytes) : HttpContent
    {
        protected override bool TryComputeLength(out long length) { length = 0; return false; }
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) => stream.WriteAsync(bytes).AsTask();
    }
}
