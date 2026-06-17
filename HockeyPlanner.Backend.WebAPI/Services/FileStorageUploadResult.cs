namespace HockeyPlanner.Backend.WebAPI.Services
{
    public sealed class FileStorageUploadResult
    {
        public required string PublicUrl { get; init; }
        public string? Key { get; init; }
    }
}
