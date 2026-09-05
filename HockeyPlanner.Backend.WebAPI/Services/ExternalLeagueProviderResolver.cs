using HockeyPlanner.Backend.Core.Enums;
using HockeyPlanner.Backend.Core.Exceptions;

namespace HockeyPlanner.Backend.WebAPI.Services
{
    public sealed class ExternalLeagueProviderResolver : IExternalLeagueProviderResolver
    {
        private readonly IReadOnlyDictionary<ExternalLeagueProvider, IExternalLeagueProvider> _providers;

        public ExternalLeagueProviderResolver(IEnumerable<IExternalLeagueProvider> providers)
        {
            _providers = providers.ToDictionary(value => value.Provider);
        }

        public IExternalLeagueProvider Resolve(ExternalLeagueProvider provider)
        {
            return _providers.TryGetValue(provider, out var implementation)
                ? implementation
                : throw new BusinessRuleException("Указанный провайдер внешней лиги не поддерживается.");
        }
    }
}
