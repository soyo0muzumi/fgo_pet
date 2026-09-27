using System.Collections.Concurrent;
using System.Net.Http;
using FgoPet.Core.Dialogue;
using FgoPet.Core.Settings;
using FgoPet.Infrastructure.Providers;

namespace FgoPet.App.Dialogue;

/// <summary>Route-local capacity resolution; a late result can only populate its own route.</summary>
public sealed class ModelContextResolver(
    Func<ModelConnectionSettings, IChatProvider> providerFactory, TimeProvider? clock = null) : IModelContextResolver
{
    private readonly ConcurrentDictionary<ModelRouteKey, (ModelContextLimit Limit, DateTimeOffset Expires)> _cache = new();
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;

    public async Task<ModelContextLimit> ResolveAsync(ModelConnectionSettings settings, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var route = ModelRouteKey.From(settings);
        if (settings.ContextWindowOverride is int capacity)
            return new(route, capacity, null, ContextLimitSource.Override, "manual");
        if (_cache.TryGetValue(route, out var cached) && cached.Expires > _clock.GetUtcNow()) return cached.Limit;
        ModelContextLimit? limit = null;
        // The same clock governs both the metadata deadline and cache expiry. Keep the
        // production five-second limit while allowing tests to advance time explicitly.
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5), _clock);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, deadline.Token);
        try
        {
            var models = await providerFactory(settings).ListModelsAsync(timeout.Token).WaitAsync(timeout.Token).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            var model = models.FirstOrDefault(item => item.Id == settings.ModelId);
            if (model?.ContextWindowTokens is > 0 && (model.MaxOutputTokens is null ||
                model.MaxOutputTokens > 0 && model.MaxOutputTokens <= model.ContextWindowTokens))
                limit = new(route, model.ContextWindowTokens.Value, model.MaxOutputTokens,
                    ContextLimitSource.ProviderMetadata, "provider-model-list");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { }
        catch (ProviderRequestException) { }
        catch (HttpRequestException) { }
        cancellationToken.ThrowIfCancellationRequested();
        limit ??= KnownModelContextCatalog.Find(settings)
            ?? new ModelContextLimit(route, 8192, null, ContextLimitSource.ConservativeFallback, "fallback-v1");
        if (_cache.Count >= 64) _cache.Clear();
        _cache[route] = (limit, _clock.GetUtcNow().AddMinutes(30));
        return limit;
    }
}
