using HockeyPlanner.Backend.Application.Abstractions.Services;
using HockeyPlanner.Backend.Core.Exceptions;
using HockeyPlanner.Backend.Shared.Models.Teams;
using HockeyPlanner.Backend.WebAPI.Models.Teams;

namespace HockeyPlanner.Backend.WebAPI.Services;

// Narrow orchestration around the existing WebAPI storage abstraction; no EF or role decisions.
public sealed class TeamMediaUploadService(ITeamMediaService teams, IFileStorageService storage) : ITeamMediaUploadService
{
    public async Task<TeamDto> UploadTeamMedia(Guid id, Guid actorUserId, IFormFile file, bool isCover, CancellationToken cancellationToken)
    {
        await teams.EnsureCanUploadTeamMedia(id, actorUserId, cancellationToken);
        ValidateImageFile(file);
        try
        {
            var url = await Upload(id, file, FileStorageFolders.Teams, cancellationToken);
            return await teams.SaveTeamMedia(id, actorUserId, url, isCover, cancellationToken);
        }
        catch (BusinessRuleException) { throw; }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            throw new HttpRequestException("Не удалось загрузить изображение команды.");
        }
    }

    public async Task<UploadTeamImageResponse> UploadNewsImage(Guid id, Guid actorUserId, IFormFile file, CancellationToken cancellationToken)
    {
        await teams.EnsureCanUploadNewsImage(id, actorUserId, cancellationToken);
        ValidateImageFile(file);
        try
        {
            return new UploadTeamImageResponse { ImageUrl = await Upload(id, file, FileStorageFolders.News, cancellationToken) };
        }
        catch (BusinessRuleException) { throw; }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            throw new HttpRequestException("Не удалось загрузить изображение новости.");
        }
    }

    private async Task<string> Upload(Guid id, IFormFile file, string folder, CancellationToken cancellationToken)
    {
        await using var stream = file.OpenReadStream();
        var result = await storage.UploadAsync(new FileStorageUploadRequest
        {
            Content = stream,
            FileName = file.FileName,
            ContentType = file.ContentType,
            Folder = folder,
            ScopeId = id.ToString("N")
        }, cancellationToken);
        return result.PublicUrl;
    }

    private static void ValidateImageFile(IFormFile file)
    {
        if (file == null || file.Length == 0)
            throw new BusinessRuleException("Файл изображения не передан.");
        if (file.Length > 5 * 1024 * 1024)
            throw new BusinessRuleException("Размер файла не должен превышать 5 МБ.");
        if (string.IsNullOrWhiteSpace(file.ContentType) || !file.ContentType.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
            throw new BusinessRuleException("Нужен файл изображения.");
        var extension = Path.GetExtension(file.FileName).ToLowerInvariant();
        var allowedExtensions = new HashSet<string> { ".jpg", ".jpeg", ".png", ".webp", ".gif" };
        if (!allowedExtensions.Contains(extension))
            throw new BusinessRuleException("Поддерживаются форматы JPG, PNG, WEBP, GIF.");
    }
}
