using HockeyPlanner.Backend.Core.Exceptions;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace HockeyPlanner.Backend.WebAPI.Services
{
    public class ImageKitUploader : IImageKitUploader
    {
        private const string UploadEndpoint = "https://upload.imagekit.io/api/v1/files/upload";
        private readonly IHttpClientFactory _httpClientFactory;
        private readonly IConfiguration _configuration;

        public ImageKitUploader(IHttpClientFactory httpClientFactory, IConfiguration configuration)
        {
            _httpClientFactory = httpClientFactory;
            _configuration = configuration;
        }

        public async Task<string> UploadAsync(Stream stream, string fileName, string folder, CancellationToken cancellationToken)
        {
            var privateKey = _configuration["ImageKit:PrivateKey"] ?? Environment.GetEnvironmentVariable("IMAGEKIT_PRIVATE_KEY");

            if (string.IsNullOrWhiteSpace(privateKey))
                throw new BusinessRuleException("Не настроен ключ ImageKit (IMAGEKIT_PRIVATE_KEY)");

            using var content = new MultipartFormDataContent();
            using var streamContent = new StreamContent(stream);
            streamContent.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
            content.Add(streamContent, "file", fileName);
            content.Add(new StringContent(fileName), "fileName");
            content.Add(new StringContent("true"), "useUniqueFileName");
            content.Add(new StringContent(folder), "folder");

            using var request = new HttpRequestMessage(HttpMethod.Post, UploadEndpoint);
            request.Content = content;

            var token = Convert.ToBase64String(Encoding.ASCII.GetBytes($"{privateKey}:"));
            request.Headers.Authorization = new AuthenticationHeaderValue("Basic", token);

            var client = _httpClientFactory.CreateClient(nameof(ImageKitUploader));
            using var response = await client.SendAsync(request, cancellationToken);
            var responseText = await response.Content.ReadAsStringAsync(cancellationToken);

            if (!response.IsSuccessStatusCode)
                throw new BusinessRuleException($"Ошибка загрузки изображения в ImageKit: {(int)response.StatusCode}");

            using var doc = JsonDocument.Parse(responseText);
            var url = doc.RootElement.GetProperty("url").GetString();

            if (string.IsNullOrWhiteSpace(url))
                throw new BusinessRuleException("ImageKit не вернул ссылку на загруженное изображение");

            return url;
        }
    }
}
