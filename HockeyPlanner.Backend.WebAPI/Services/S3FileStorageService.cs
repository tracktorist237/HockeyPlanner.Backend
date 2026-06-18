using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using HockeyPlanner.Backend.Core.Exceptions;
using System.Text.RegularExpressions;

namespace HockeyPlanner.Backend.WebAPI.Services
{
    public sealed partial class S3FileStorageService : IFileStorageService
    {
        private static readonly HashSet<string> AllowedFolders =
        [
            FileStorageFolders.Avatars,
            FileStorageFolders.Teams,
            FileStorageFolders.Players,
            FileStorageFolders.Events,
            FileStorageFolders.News,
            FileStorageFolders.Chat,
            FileStorageFolders.Uploads
        ];

        private readonly IConfiguration _configuration;
        private readonly ILogger<S3FileStorageService> _logger;

        public S3FileStorageService(
            IConfiguration configuration,
            ILogger<S3FileStorageService> logger)
        {
            _configuration = configuration;
            _logger = logger;
        }

        public async Task<FileStorageUploadResult> UploadAsync(
            FileStorageUploadRequest request,
            CancellationToken cancellationToken)
        {
            var bucket = RequiredSetting("S3:Bucket");
            var publicBaseUrl = RequiredSetting("S3:PublicBaseUrl").TrimEnd('/');
            var key = BuildObjectKey(request);

            try
            {
                using var client = CreateClient();
                var putRequest = new PutObjectRequest
                {
                    BucketName = bucket,
                    Key = key,
                    InputStream = request.Content,
                    ContentType = string.IsNullOrWhiteSpace(request.ContentType)
                        ? "application/octet-stream"
                        : request.ContentType
                };

                await client.PutObjectAsync(putRequest, cancellationToken);

                return new FileStorageUploadResult
                {
                    Key = key,
                    PublicUrl = $"{publicBaseUrl}/{key}"
                };
            }
            catch (BusinessRuleException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(
                    ex,
                    "S3 upload failed for bucket {Bucket} and key {Key}",
                    bucket,
                    key);

                throw new BusinessRuleException("Не удалось загрузить файл в S3-хранилище. Попробуйте ещё раз.");
            }
        }

        public async Task DeleteAsync(string key, CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(key))
            {
                return;
            }

            var bucket = RequiredSetting("S3:Bucket");

            try
            {
                using var client = CreateClient();
                await client.DeleteObjectAsync(bucket, key, cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(
                    ex,
                    "S3 delete failed for bucket {Bucket} and key {Key}",
                    bucket,
                    key);

                throw new BusinessRuleException("Не удалось удалить файл из S3-хранилища.");
            }
        }

        private IAmazonS3 CreateClient()
        {
            var endpoint = RequiredSetting("S3:Endpoint");
            var region = RequiredSetting("S3:Region");
            var accessKey = RequiredSetting("S3:AccessKey");
            var secretKey = RequiredSetting("S3:SecretKey");

            var config = new AmazonS3Config
            {
                ServiceURL = endpoint,
                AuthenticationRegion = region,
                ForcePathStyle = bool.TryParse(_configuration["S3:ForcePathStyle"], out var forcePathStyle)
                    && forcePathStyle
            };

            return new AmazonS3Client(new BasicAWSCredentials(accessKey, secretKey), config);
        }

        private string RequiredSetting(string key)
        {
            var value = _configuration[key];
            if (string.IsNullOrWhiteSpace(value))
            {
                throw new BusinessRuleException($"Не настроен параметр хранилища файлов: {key}");
            }

            return value;
        }

        private static string BuildObjectKey(FileStorageUploadRequest request)
        {
            var folder = NormalizeFolder(request.Folder);
            var extension = NormalizeExtension(request.FileName);
            var fileName = $"{Guid.NewGuid():N}{extension}";

            if (string.IsNullOrWhiteSpace(request.ScopeId))
            {
                return $"{folder}/{fileName}";
            }

            return $"{folder}/{SafePathSegment(request.ScopeId)}/{fileName}";
        }

        private static string NormalizeFolder(string folder)
        {
            var normalized = folder.Trim().Trim('/').ToLowerInvariant();
            return AllowedFolders.Contains(normalized)
                ? normalized
                : FileStorageFolders.Uploads;
        }

        private static string NormalizeExtension(string fileName)
        {
            var extension = Path.GetExtension(fileName);
            if (string.IsNullOrWhiteSpace(extension))
            {
                return ".bin";
            }

            extension = extension.ToLowerInvariant();
            return SafeExtensionRegex().IsMatch(extension)
                ? extension
                : ".bin";
        }

        private static string SafePathSegment(string value)
        {
            var sanitized = SafePathSegmentRegex()
                .Replace(value.Trim().ToLowerInvariant(), "-")
                .Trim('-');

            return string.IsNullOrWhiteSpace(sanitized)
                ? "default"
                : sanitized;
        }

        [GeneratedRegex(@"^\.[a-z0-9]{1,12}$")]
        private static partial Regex SafeExtensionRegex();

        [GeneratedRegex(@"[^a-z0-9_-]+")]
        private static partial Regex SafePathSegmentRegex();
    }
}
