namespace HockeyPlanner.Backend.WebAPI.Services
{
    public sealed class FileStorageUploadRequest
    {
        public required Stream Content { get; init; }
        public required string FileName { get; init; }
        public string? ContentType { get; init; }
        public string Folder { get; init; } = FileStorageFolders.Uploads;
        public string? ScopeId { get; init; }
    }
}
