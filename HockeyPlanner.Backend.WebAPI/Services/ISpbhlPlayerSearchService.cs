using HockeyPlanner.Backend.WebAPI.Models.Spbhl;

namespace HockeyPlanner.Backend.WebAPI.Services
{
    public interface ISpbhlPlayerSearchService
    {
        Task<SpbhlPlayersSearchResponse> SearchPlayers(string fullName, string? birthYear, int page, CancellationToken cancellationToken);
    }
}
