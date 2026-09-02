using HockeyPlanner.Backend.WebAPI.Services;

namespace HockeyPlanner.Backend.IntegrationTests.Infrastructure;

public sealed class SpyFileStorageService : IFileStorageService
{
    private int _uploadCallCount;
    private int _deleteCallCount;

    public string PublicUrl { get; init; } = "https://test.invalid/avatars/updated.png";

    public int UploadCallCount => Volatile.Read(ref _uploadCallCount);
    public int DeleteCallCount => Volatile.Read(ref _deleteCallCount);

    public Task<FileStorageUploadResult> UploadAsync(
        FileStorageUploadRequest request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Interlocked.Increment(ref _uploadCallCount);
        return Task.FromResult(new FileStorageUploadResult
        {
            PublicUrl = PublicUrl,
            Key = $"test/{request.FileName}",
        });
    }

    public Task DeleteAsync(string key, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Interlocked.Increment(ref _deleteCallCount);
        return Task.CompletedTask;
    }
}
