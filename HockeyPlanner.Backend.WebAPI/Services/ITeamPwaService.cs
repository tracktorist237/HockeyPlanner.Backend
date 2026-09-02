namespace HockeyPlanner.Backend.WebAPI.Services
{
    public interface ITeamPwaService
    {
        Task<TeamPwaManifestResult?> GetManifestAsync(
            Guid teamId,
            string? requestedName,
            CancellationToken cancellationToken);

        Task<TeamPwaIconResult?> GetIconAsync(
            Guid teamId,
            int size,
            CancellationToken cancellationToken);

        Task<TeamPwaIconResult?> GetOriginalLogoAsync(
            Guid teamId,
            CancellationToken cancellationToken);
    }

    public sealed record TeamPwaManifestResult(byte[] Content, string EntityTag);

    public sealed record TeamPwaIconResult(byte[] Content, string ContentType, string EntityTag);
}
