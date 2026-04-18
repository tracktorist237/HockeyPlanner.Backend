namespace HockeyPlanner.Backend.WebAPI.Services
{
    public interface IImageKitUploader
    {
        Task<string> UploadAsync(Stream stream, string fileName, string folder, CancellationToken cancellationToken);
    }
}
