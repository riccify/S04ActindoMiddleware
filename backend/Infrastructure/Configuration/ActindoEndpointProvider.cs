using System.Threading;
using System.Threading.Tasks;
using ActindoMiddleware.Application.Configuration;

namespace ActindoMiddleware.Infrastructure.Configuration;

public sealed class ActindoEndpointProvider : IActindoEndpointProvider
{
    private readonly ISettingsStore _settingsStore;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private ActindoEndpointSet? _cache;

    public ActindoEndpointProvider(ISettingsStore settingsStore)
    {
        _settingsStore = settingsStore;
    }

    public async Task<ActindoEndpointSet> GetAsync(CancellationToken cancellationToken = default)
    {
        if (_cache is not null)
            return _cache;

        await _lock.WaitAsync(cancellationToken);
        try
        {
            if (_cache is not null)
                return _cache;

            var settings = await _settingsStore.GetActindoSettingsAsync(cancellationToken);
            _cache = ActindoEndpointSet.FromDictionary(settings.Endpoints ?? new(), settings.ActindoBaseUrl);
            return _cache;
        }
        finally
        {
            _lock.Release();
        }
    }

    public void Invalidate()
    {
        _cache = null;
    }
}

