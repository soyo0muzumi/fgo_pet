using FgoPet.App.Dialogue;
using FgoPet.Core.Dialogue;
using FgoPet.Core.Settings;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace FgoPet.App.Tests.Dialogue;

public sealed class ModelContextResolverTests
{
    [Fact]
    public async Task Late_route_a_result_does_not_replace_route_b_cache()
    {
        var clock = new FakeTimeProvider();
        var a = new TaskCompletionSource<IReadOnlyList<ProviderModel>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var b = new TaskCompletionSource<IReadOnlyList<ProviderModel>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var resolver = new ModelContextResolver(settings => new DelayedModels(settings.BaseUrl.Contains("a.test") ? a.Task : b.Task), clock);
        var settingsA = new ModelConnectionSettings("test", "https://a.test/v1", "same");
        var settingsB = new ModelConnectionSettings("test", "https://b.test/v1", "same");
        var old = resolver.ResolveAsync(settingsA, default);
        var current = resolver.ResolveAsync(settingsB, default);
        b.SetResult([new("same", contextWindowTokens: 131072)]);
        Assert.Equal(131072, (await current.WaitAsync(TimeSpan.FromSeconds(5))).WindowTokens);
        a.SetResult([new("same", contextWindowTokens: 32768)]);
        Assert.Equal(32768, (await old.WaitAsync(TimeSpan.FromSeconds(5))).WindowTokens);
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

    private sealed class DelayedModels(Task<IReadOnlyList<ProviderModel>> result,
        Action<CancellationToken>? observeToken = null) : IChatProvider
    {
        public string ProviderId => "test";
        public string ModelId => "same";
        public Task<IReadOnlyList<ProviderModel>> ListModelsAsync(CancellationToken cancellationToken)
        {
            observeToken?.Invoke(cancellationToken);
            // Deliberately ignore cancellation: the resolver must bound an uncooperative provider.
            return result;
        }
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

    [Fact]
    public async Task Metadata_deadline_is_exactly_five_seconds_on_the_injected_clock()
    {
        var clock = new FakeTimeProvider();
        var pending = new TaskCompletionSource<IReadOnlyList<ProviderModel>>(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationToken providerToken = default;
        var resolver = new ModelContextResolver(_ => new DelayedModels(pending.Task, token => providerToken = token), clock);
        var resolution = resolver.ResolveAsync(new("test", "https://example.test", "same"), default);

        clock.Advance(TimeSpan.FromMilliseconds(4999));
        Assert.False(resolution.IsCompleted);
        Assert.False(providerToken.IsCancellationRequested);
        clock.Advance(TimeSpan.FromMilliseconds(1));

        var limit = await resolution.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(providerToken.IsCancellationRequested);
        Assert.Equal(ContextLimitSource.ConservativeFallback, limit.Source);
        Assert.Equal(8192, limit.WindowTokens);
        pending.SetResult([new("same", contextWindowTokens: 131072)]);
    }

    [Fact]
    public async Task Caller_cancellation_during_metadata_does_not_cache_a_fallback()
    {
        var clock = new FakeTimeProvider();
        var pending = new TaskCompletionSource<IReadOnlyList<ProviderModel>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        CancellationToken providerToken = default;
        var resolver = new ModelContextResolver(_ => ++calls == 1
            ? new DelayedModels(pending.Task, token => providerToken = token)
            : new ModelsProvider(32768), clock);
        var settings = new ModelConnectionSettings("test", "https://example.test", "same");
        using var caller = new CancellationTokenSource();
        var resolution = resolver.ResolveAsync(settings, caller.Token);
        caller.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => resolution.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.True(providerToken.IsCancellationRequested);
        var fresh = await resolver.ResolveAsync(settings, default);
        Assert.Equal(ContextLimitSource.ProviderMetadata, fresh.Source);
        Assert.Equal(32768, fresh.WindowTokens);
        Assert.Equal(2, calls);
        pending.SetResult([new("same", contextWindowTokens: 131072)]);
        Assert.Equal(32768, (await resolver.ResolveAsync(settings, default)).WindowTokens);
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task Successful_metadata_cache_expires_at_thirty_minutes_on_the_same_clock()
    {
        var clock = new FakeTimeProvider();
        var calls = 0;
        var resolver = new ModelContextResolver(_ => new ModelsProvider(++calls == 1 ? 32768 : 131072), clock);
        var settings = new ModelConnectionSettings("test", "https://example.test", "same");
        Assert.Equal(32768, (await resolver.ResolveAsync(settings, default)).WindowTokens);

        clock.Advance(TimeSpan.FromMinutes(30) - TimeSpan.FromMilliseconds(1));
        Assert.Equal(32768, (await resolver.ResolveAsync(settings, default)).WindowTokens);
        Assert.Equal(1, calls);
        clock.Advance(TimeSpan.FromMilliseconds(1));
        Assert.Equal(131072, (await resolver.ResolveAsync(settings, default)).WindowTokens);
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task Late_metadata_after_timeout_cannot_replace_the_cached_fallback()
    {
        var clock = new FakeTimeProvider();
        var pending = new TaskCompletionSource<IReadOnlyList<ProviderModel>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        var resolver = new ModelContextResolver(_ => ++calls == 1 ? new DelayedModels(pending.Task) : new ModelsProvider(32768), clock);
        var settings = new ModelConnectionSettings("test", "https://example.test", "same");
        var resolution = resolver.ResolveAsync(settings, default);
        clock.Advance(TimeSpan.FromSeconds(5));
        Assert.Equal(ContextLimitSource.ConservativeFallback, (await resolution.WaitAsync(TimeSpan.FromSeconds(5))).Source);

        pending.SetResult([new("same", contextWindowTokens: 131072)]);
        var cached = await resolver.ResolveAsync(settings, default);
        Assert.Equal(ContextLimitSource.ConservativeFallback, cached.Source);
        Assert.Equal(8192, cached.WindowTokens);
        Assert.Equal(1, calls);
        clock.Advance(TimeSpan.FromMinutes(30));
        var fresh = await resolver.ResolveAsync(settings, default);
        Assert.Equal(ContextLimitSource.ProviderMetadata, fresh.Source);
        Assert.Equal(32768, fresh.WindowTokens);
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task Cancelling_one_route_does_not_cancel_another_pending_route()
    {
        var clock = new FakeTimeProvider();
        var a = new TaskCompletionSource<IReadOnlyList<ProviderModel>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var b = new TaskCompletionSource<IReadOnlyList<ProviderModel>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var resolver = new ModelContextResolver(settings => new DelayedModels(settings.BaseUrl.Contains("a.test") ? a.Task : b.Task), clock);
        var settingsA = new ModelConnectionSettings("test", "https://a.test", "same");
        var settingsB = new ModelConnectionSettings("test", "https://b.test", "same");
        using var callerA = new CancellationTokenSource();
        var resultA = resolver.ResolveAsync(settingsA, callerA.Token);
        var resultB = resolver.ResolveAsync(settingsB, default);
        callerA.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => resultA.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.False(resultB.IsCompleted);

        b.SetResult([new("same", contextWindowTokens: 131072)]);
        Assert.Equal(131072, (await resultB.WaitAsync(TimeSpan.FromSeconds(5))).WindowTokens);
        a.SetResult([new("same", contextWindowTokens: 32768)]);
        Assert.Equal(32768, (await resolver.ResolveAsync(settingsA, default)).WindowTokens);
        Assert.Equal(131072, (await resolver.ResolveAsync(settingsB, default)).WindowTokens);
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
