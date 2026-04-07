using HockeyPlanner.Backend.Core.Exceptions;
using Microsoft.Extensions.Logging;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace HockeyPlanner.Backend.WebAPI.Services
{
    public class ImageKitUploader : IImageKitUploader
    {
        private const string UploadEndpoint = "https://upload.imagekit.io/api/v1/files/upload";
        private const int MaxUploadAttempts = 3;

        private readonly IHttpClientFactory _httpClientFactory;
        private readonly IConfiguration _configuration;
        private readonly ILogger<ImageKitUploader> _logger;

        public ImageKitUploader(
            IHttpClientFactory httpClientFactory,
            IConfiguration configuration,
            ILogger<ImageKitUploader> logger)
        {
            _httpClientFactory = httpClientFactory;
            _configuration = configuration;
            _logger = logger;
        }

        public async Task<string> UploadAsync(
            Stream stream,
            string fileName,
            string folder,
            CancellationToken cancellationToken)
        {
            var privateKey = _configuration["ImageKit:PrivateKey"]
                ?? Environment.GetEnvironmentVariable("IMAGEKIT_PRIVATE_KEY");

            if (string.IsNullOrWhiteSpace(privateKey))
            {
                throw new BusinessRuleException("Не настроен ключ ImageKit (IMAGEKIT_PRIVATE_KEY)");
            }

            await using var bufferedStream = new MemoryStream();
            await stream.CopyToAsync(bufferedStream, cancellationToken);

            var token = Convert.ToBase64String(Encoding.ASCII.GetBytes($"{privateKey}:"));
            var client = _httpClientFactory.CreateClient(nameof(ImageKitUploader));
            var delay = TimeSpan.FromMilliseconds(400);
            string? responseText = null;

            for (var attempt = 1; attempt <= MaxUploadAttempts; attempt++)
            {
                bufferedStream.Position = 0;

                using var content = BuildMultipartContent(bufferedStream, fileName, folder);
                using var request = new HttpRequestMessage(HttpMethod.Post, UploadEndpoint)
                {
                    Content = content,
                    Version = HttpVersion.Version11,
                    VersionPolicy = HttpVersionPolicy.RequestVersionOrLower
                };
                request.Headers.Authorization = new AuthenticationHeaderValue("Basic", token);

                try
                {
                    using var response = await client.SendAsync(
                        request,
                        HttpCompletionOption.ResponseHeadersRead,
                        cancellationToken);

                    responseText = await response.Content.ReadAsStringAsync(cancellationToken);
                    if (!response.IsSuccessStatusCode)
                    {
                        throw new BusinessRuleException(
                            $"Ошибка загрузки изображения в ImageKit: {(int)response.StatusCode}");
                    }

                    break;
                }
                catch (Exception ex) when (IsTransient(ex) && attempt < MaxUploadAttempts)
                {
                    _logger.LogWarning(
                        ex,
                        "ImageKit upload transient failure on attempt {Attempt}/{Total} for file {FileName}",
                        attempt,
                        MaxUploadAttempts,
                        fileName);

                    await Task.Delay(delay, cancellationToken);
                    delay += delay;
                }
                catch (HttpRequestException ex)
                {
                    _logger.LogError(ex, "ImageKit upload failed for file {FileName}", fileName);
                    throw new BusinessRuleException("Временная ошибка сети при загрузке аватарки. Попробуйте ещё раз.");
                }
            }

            if (string.IsNullOrWhiteSpace(responseText))
            {
                throw new BusinessRuleException("ImageKit не вернул ответ при загрузке изображения");
            }

            using var doc = JsonDocument.Parse(responseText);
            var url = doc.RootElement.GetProperty("url").GetString();

            if (string.IsNullOrWhiteSpace(url))
            {
                throw new BusinessRuleException("ImageKit не вернул ссылку на загруженное изображение");
            }

            return url;
        }

        private static MultipartFormDataContent BuildMultipartContent(
            MemoryStream stream,
            string fileName,
            string folder)
        {
            var content = new MultipartFormDataContent();
            var streamContent = new StreamContent(stream, (int)stream.Length);
            streamContent.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");

            content.Add(streamContent, "file", fileName);
            content.Add(new StringContent(fileName), "fileName");
            content.Add(new StringContent("true"), "useUniqueFileName");
            content.Add(new StringContent(folder), "folder");

            return content;
        }

        private static bool IsTransient(Exception ex)
        {
            return ex switch
            {
                HttpRequestException => true,
                IOException => true,
                TaskCanceledException => true,
                _ => ex.InnerException != null && IsTransient(ex.InnerException)
            };
        }
    }
}
