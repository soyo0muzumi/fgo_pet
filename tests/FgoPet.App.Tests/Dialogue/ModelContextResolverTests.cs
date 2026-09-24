using FgoPet.App.Dialogue;
using FgoPet.Core.Dialogue;
using FgoPet.Core.Settings;
using Xunit;

namespace FgoPet.App.Tests.Dialogue;

public sealed class ModelContextResolverTests
{
    [Fact]
    public async Task Late_route_a_result_does_not_replace_route_b_cache()
    {
        var a = new TaskCompletionSource<IReadOnlyList<ProviderModel>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var b = new TaskCompletionSource<IReadOnlyList<ProviderModel>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var resolver = new ModelContextResolver(settings => new DelayedModels(settings.BaseUrl.Contains("a.test") ? a.Task : b.Task));
        var settingsA = new ModelConnectionSettings("test", "https://a.test/v1", "same");
        var settingsB = new ModelConnectionSettings("test", "https://b.test/v1", "same");
        var old = resolver.ResolveAsync(settingsA, default);
        var current = resolver.ResolveAsync(settingsB, default);
        b.SetResult([new("same", contextWindowTokens: 131072)]);
        Assert.Equal(131072, (await current).WindowTokens);
        a.SetResult([new("same", contextWindowTokens: 32768)]);
        Assert.Equal(32768, (await old).WindowTokens);
        Assert.Equal(131072, (await resolver.ResolveAsync(settingsB, default)).WindowTokens);
    }

    [Fact]
    public async Task Verified_catalog_is_used_only_after_missing_provider_metadata()
    {
        var resolver = new ModelContextResolver(_ => new ModelsProvider(null));
        var limit = await resolver.ResolveAsync(new("openai", "https://api.openai.com/v1", "gpt-4.1-mini"), default);
        Assert.Equal(ContextLimitSource.KnownModel, limit.Source);
        Assert.Equal(1047576, limit.WindowTokens);
        Assert.Equal(32768, limit.MaxOutputTokens);
    }

    private sealed class DelayedModels(Task<IReadOnlyList<ProviderModel>> result) : IChatProvider
    {
        public string ProviderId => "test";
        public string ModelId => "same";
        public Task<IReadOnlyList<ProviderModel>> ListModelsAsync(CancellationToken cancellationToken) => result;
        public IAsyncEnumerable<ChatStreamChunk> StreamAsync(ChatRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    [Fact]
    public async Task Override_wins_without_querying_provider()
    {
        var resolver = new ModelContextResolver(_ => throw new InvalidOperationException("must not query"));
        var limit = await resolver.ResolveAsync(new("custom-openai-compatible", "https://example.test/v1",
            "unknown", contextWindowOverride: 32768), CancellationToken.None);
        Assert.Equal(32768, limit.WindowTokens);
        Assert.Equal(ContextLimitSource.Override, limit.Source);
    }

    [Fact]
    public async Task Same_model_on_different_routes_does_not_share_metadata()
    {
        var resolver = new ModelContextResolver(settings => new ModelsProvider(
            settings.BaseUrl.Contains("small", StringComparison.Ordinal) ? 32768 : 131072));
        var small = await resolver.ResolveAsync(new("test", "https://small.test/v1", "same"), default);
        var large = await resolver.ResolveAsync(new("test", "https://large.test/v1", "same"), default);
        Assert.Equal(32768, small.WindowTokens);
        Assert.Equal(131072, large.WindowTokens);
        Assert.NotEqual(small.Route, large.Route);
        Assert.Equal(ContextLimitSource.ProviderMetadata, small.Source);
    }

    [Fact]
    public async Task Unknown_custom_endpoint_is_labelled_fallback_even_for_known_model_name()
    {
        var resolver = new ModelContextResolver(_ => new ModelsProvider(null));
        var limit = await resolver.ResolveAsync(new("openai", "https://proxy.test/v1", "gpt-4.1-mini"), default);
        Assert.Equal(8192, limit.WindowTokens);
        Assert.Equal(ContextLimitSource.ConservativeFallback, limit.Source);
    }

    [Fact]
    public void Route_normalizes_host_and_trailing_slash_but_retains_path_and_config()
    {
        var a = ModelRouteKey.From(new("test", "https://EXAMPLE.test:443/v1", "m"));
        var b = ModelRouteKey.From(new("test", "https://example.test/v1/", "m"));
        Assert.Equal(a, b);
        Assert.NotEqual(a, ModelRouteKey.From(new("test", "https://example.test/V1", "m")));
        Assert.NotEqual(a, ModelRouteKey.From(new("test", "https://example.test/v1", "m", maxOutputTokens: 4096)));
        Assert.DoesNotContain("example", a.EndpointKey, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Cancelled_metadata_is_not_cached_as_fallback()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var resolver = new ModelContextResolver(_ => new ModelsProvider(32768));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => resolver.ResolveAsync(
            new("test", "https://example.test", "same"), cts.Token));
    }

    private sealed class ModelsProvider(int? window) : IChatProvider
    {
        public string ProviderId => "test";
        public string ModelId => "same";
        public Task<IReadOnlyList<ProviderModel>> ListModelsAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<ProviderModel>>([new("same", contextWindowTokens: window)]);
        public IAsyncEnumerable<ChatStreamChunk> StreamAsync(ChatRequest request, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }
}
