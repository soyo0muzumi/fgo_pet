using FgoPet.App.Dialogue;
using FgoPet.Core.Dialogue;
using FgoPet.Core.Settings;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace FgoPet.App.Tests.Dialogue;

public sealed class ConfiguredModelCatalogTests
{
    private static ModelConnectionSettings Connection(string endpoint = "https://a.example/v1") => new("openai", endpoint, "saved");

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Concurrent_completion_and_invalidation_never_use_a_disposed_cancellation_source(bool dispose)
    {
        // Two threads race at the actual lifecycle boundary. Completion may dispose
        // the source after Invalidate detaches it but before it calls Cancel.
        for (var attempt = 0; attempt < 2500; attempt++)
        {
            var response = new TaskCompletionSource<IReadOnlyList<ProviderModel>>();
            using var catalog = new ConfiguredModelCatalog(() => Connection(), (_, _) => response.Task);
            Task refresh = Task.CompletedTask;
            await Task.Run(() => { refresh = catalog.RefreshAsync(); });
            using var start = new Barrier(2);
            await Task.WhenAll(
                Task.Run(() => { Assert.True(start.SignalAndWait(TimeSpan.FromSeconds(5))); response.SetResult([new("old", "Old")]); }),
                Task.Run(() => { Assert.True(start.SignalAndWait(TimeSpan.FromSeconds(5))); if (dispose) catalog.Dispose(); else catalog.Invalidate(); }));
            await refresh;
            Assert.False(catalog.IsRefreshing);
            Assert.Empty(catalog.Models);
        }
    }

    [Fact]
    public async Task Coalesces_refresh_and_keeps_selected_model_until_explicit_selection()
    {
        var clock = new FakeTimeProvider(); var calls = 0;
        var response = new TaskCompletionSource<IReadOnlyList<ProviderModel>>();
        using var catalog = new ConfiguredModelCatalog(() => Connection(), (_, _) => { calls++; return response.Task; }, clock);
        var first = catalog.RefreshAsync(); var second = catalog.RefreshAsync();
        Assert.True(catalog.IsRefreshing); Assert.Equal(1, calls);
        response.SetResult([new("another", "Another")]);
        await Task.WhenAll(first, second);
        Assert.Equal("saved", catalog.SelectedModelId);
        await catalog.RefreshAsync(); Assert.Equal(1, calls);
        clock.Advance(ConfiguredModelCatalog.CacheLifetime);
        await catalog.RefreshAsync(); Assert.Equal(2, calls);
    }

    [Fact]
    public async Task New_endpoint_rejects_old_success_even_when_provider_ignores_cancellation()
    {
        var connection = Connection();
        var old = new TaskCompletionSource<IReadOnlyList<ProviderModel>>();
        using var catalog = new ConfiguredModelCatalog(() => connection,
            (c, _) => c.BaseUrl.Contains("a.example") ? old.Task : Task.FromResult<IReadOnlyList<ProviderModel>>([new("b", "B")]));
        var first = catalog.RefreshAsync();
        connection = Connection("https://b.example/v1");
        Assert.Empty(catalog.Models);
        await catalog.RefreshAsync(); old.TrySetResult([new("a", "A")]); await first;
        Assert.Equal("b", Assert.Single(catalog.Models).Id);
    }

    [Fact]
    public async Task Failure_keeps_same_connection_cache_and_throttles_retry()
    {
        var clock = new FakeTimeProvider(); var calls = 0;
        using var catalog = new ConfiguredModelCatalog(() => Connection(), (_, _) =>
        {
            if (++calls > 1) throw new System.Net.Http.HttpRequestException("must not appear in UI");
            return Task.FromResult<IReadOnlyList<ProviderModel>>([new("a", "A")]);
        }, clock);
        await catalog.RefreshAsync(); clock.Advance(ConfiguredModelCatalog.CacheLifetime);
        await catalog.RefreshAsync(); Assert.Single(catalog.Models);
        Assert.DoesNotContain("must not", catalog.Status);
        await catalog.RefreshAsync(); Assert.Equal(2, calls);
        clock.Advance(TimeSpan.FromSeconds(30)); await catalog.RefreshAsync(); Assert.Equal(3, calls);
    }

    [Fact]
    public async Task Timeout_releases_busy_state_without_applying_a_late_result()
    {
        var clock = new FakeTimeProvider();
        var response = new TaskCompletionSource<IReadOnlyList<ProviderModel>>();
        using var catalog = new ConfiguredModelCatalog(() => Connection(), (_, _) => response.Task, clock);
        var refresh = catalog.RefreshAsync();
        clock.Advance(TimeSpan.FromSeconds(15)); await refresh;
        Assert.False(catalog.IsRefreshing); Assert.Empty(catalog.Models);
        Assert.Contains("失败", catalog.Status);
        response.SetResult([new("late", "Late")]);
        Assert.Empty(catalog.Models);
    }

    [Fact]
    public async Task Credential_invalidation_discards_late_results_and_caller_cancel_does_not_cancel_shared_refresh()
    {
        var response = new TaskCompletionSource<IReadOnlyList<ProviderModel>>();
        using var catalog = new ConfiguredModelCatalog(() => Connection(), (_, _) => response.Task);
        using var caller = new CancellationTokenSource();
        var canceled = catalog.RefreshAsync(caller.Token); var shared = catalog.RefreshAsync();
        caller.Cancel(); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => canceled);
        Assert.True(catalog.IsRefreshing);
        catalog.Invalidate(); response.TrySetResult([new("stale", "Stale")]); await shared;
        Assert.Empty(catalog.Models);
        catalog.Dispose(); await catalog.RefreshAsync(); Assert.Empty(catalog.Models);
    }
}
