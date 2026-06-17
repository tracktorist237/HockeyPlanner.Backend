namespace HockeyPlanner.Backend.WebAPI.Services
{
    public interface IFileStorageService
    {
        Task<FileStorageUploadResult> UploadAsync(
            FileStorageUploadRequest request,
            CancellationToken cancellationToken);

        Task DeleteAsync(string key, CancellationToken cancellationToken);
    }
}
