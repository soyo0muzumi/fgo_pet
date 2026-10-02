using FgoPet.Extensibility;
using Xunit;

namespace FgoPet.Foundation.Tests;

public sealed class PluginRuntimeTests
{
    [Fact]
    public async Task Activation_and_repeated_shutdown_are_owned_and_idempotent()
    {
        var plugin = new PluginCatalogTests.SamplePlugin("test.one", contributes: false);
        var runtime = new PluginRuntime(PluginCatalog.Create([plugin]));
        Assert.True((await runtime.StartAsync(CancellationToken.None)).Succeeded);
        Assert.True(runtime.IsActive("test.one"));
        await runtime.StartAsync(CancellationToken.None);
        Assert.Empty(await runtime.StopAsync());
        Assert.Empty(await runtime.StopAsync());
        await runtime.DisposeAsync();
        Assert.Equal(new[] { "start", "stop", "dispose" }, plugin.Events);
        Assert.False(runtime.IsActive("test.one"));
    }

    [Fact]
    public async Task Failed_start_cleans_the_failed_instance_and_already_started_dependencies()
    {
        var dependency = new PluginCatalogTests.SamplePlugin("test.base", contributes: false);
        var failed = new PluginCatalogTests.SamplePlugin("test.failed", ["test.base"], false) { FailStart = true };
        var runtime = new PluginRuntime(PluginCatalog.Create([failed, dependency]));
        var result = await runtime.StartAsync(CancellationToken.None);
        Assert.False(result.Succeeded);
        Assert.Equal("PLUGIN_START_FAILED", result.ErrorCode);
        Assert.Equal("test.failed", Assert.Single(result.Failures).PluginId);
        Assert.Equal(new[] { "start", "stop", "dispose" }, dependency.Events);
        Assert.Equal(new[] { "start", "stop", "dispose" }, failed.Events);
        Assert.False(runtime.IsActive("test.base"));
        Assert.DoesNotContain("secret", result.ToString());
    }

    [Fact]
    public async Task A_throwing_stop_does_not_prevent_other_plugins_or_disposal()
    {
        var first = new PluginCatalogTests.SamplePlugin("test.base", contributes: false);
        var throwing = new PluginCatalogTests.SamplePlugin("test.last", ["test.base"], false) { FailStop = true };
        var runtime = new PluginRuntime(PluginCatalog.Create([throwing, first]));
        await runtime.StartAsync(CancellationToken.None);
        var failures = await runtime.StopAsync();
        Assert.Equal("PLUGIN_STOP_FAILED", Assert.Single(failures).Code);
        Assert.Equal(new[] { "start", "stop", "dispose" }, first.Events);
        Assert.Equal(new[] { "start", "stop", "dispose" }, throwing.Events);
        Assert.DoesNotContain("secret", string.Join(',', failures));
    }

    [Fact]
    public async Task Cancelled_process_does_not_activate_plugins()
    {
        var plugin = new PluginCatalogTests.SamplePlugin("test.one", contributes: false);
        var runtime = new PluginRuntime(PluginCatalog.Create([plugin]));
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        var result = await runtime.StartAsync(cancelled.Token);
        Assert.False(result.Succeeded);
        Assert.Empty(plugin.Events);
    }

    [Fact]
    public async Task Concurrent_stop_requests_dispose_each_instance_once()
    {
        var plugin = new PluginCatalogTests.SamplePlugin("test.one", contributes: false);
        var runtime = new PluginRuntime(PluginCatalog.Create([plugin]));
        await runtime.StartAsync(CancellationToken.None);
        await Task.WhenAll(Enumerable.Range(0, 12).Select(_ => runtime.StopAsync()));
        Assert.Single(plugin.Events.Where(value => value == "dispose"));
    }

    [Fact]
    public async Task Emergency_disposal_fences_visibility_and_activation_without_losing_async_cleanup()
    {
        var plugin = new PluginCatalogTests.SamplePlugin("test.one", contributes: false);
        var runtime = new PluginRuntime(PluginCatalog.Create([plugin]));
        await runtime.StartAsync(default);
        runtime.Dispose();
        Assert.False(runtime.IsActive("test.one"));
        Assert.False((await runtime.StartAsync(default)).Succeeded);
        Assert.Equal(new[] { "start" }, plugin.Events);
        await runtime.StopAsync();
        Assert.Equal(new[] { "start", "stop", "dispose" }, plugin.Events);
    }
    [Fact]
    public async Task Stop_request_closes_capability_admission_before_async_cleanup_finishes()
    {
        var plugin = new BlockingPlugin();
        var runtime = new PluginRuntime(PluginCatalog.Create([plugin]));
        await runtime.StartAsync(default);
        var stop = runtime.StopAsync();
        await plugin.StopEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        try { Assert.False(runtime.IsActive(plugin.Manifest.Id)); }
        finally { plugin.ReleaseStop.TrySetResult(); await stop; }
    }

    [Fact]
    public async Task Readiness_is_delivered_once_only_to_active_owners_and_failures_are_isolated()
    {
        var failing = new ReadyPlugin("test.a", true);
        var healthy = new ReadyPlugin("test.b", false);
        await using var runtime = new PluginRuntime(PluginCatalog.Create([healthy, failing]));
        Assert.Empty(await runtime.NotifyApplicationReadyAsync(new(true)));
        Assert.Equal(0, healthy.Calls);
        await runtime.StartAsync(default);
        var failure = Assert.Single(await runtime.NotifyApplicationReadyAsync(new(true)));
        Assert.Equal("PLUGIN_READY_FAILED", failure.Code);
        Assert.Equal("test.a", failure.PluginId);
        Assert.Equal(1, healthy.Calls);
        Assert.Empty(await runtime.NotifyApplicationReadyAsync(new(false)));
        Assert.Equal(1, healthy.Calls);
        await runtime.StopAsync();
        Assert.Empty(await runtime.NotifyApplicationReadyAsync(new(true)));
        Assert.Equal(1, healthy.Calls);
    }

    private sealed class ReadyPlugin(string id, bool fail) : IFgoPetPlugin, IApplicationReadyObserver
    {
        public int Calls { get; private set; }
        public PluginManifest Manifest { get; } = new(id, "1.0", 1, []);
        public PluginContributions Contributions => PluginContributions.Empty;
        public ValueTask StartAsync(CancellationToken token) => ValueTask.CompletedTask;
        public ValueTask StopAsync(CancellationToken token) => ValueTask.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        public ValueTask OnApplicationReadyAsync(ApplicationReadiness readiness, CancellationToken token)
        {
            Calls++;
            if (fail) throw new InvalidOperationException("private fixture exception");
            return ValueTask.CompletedTask;
        }
    }

    private sealed class BlockingPlugin : IFgoPetPlugin
    {
        public PluginManifest Manifest { get; } = new("test.blocking", "1.0", 1, []);
        public PluginContributions Contributions => PluginContributions.Empty;
        public TaskCompletionSource StopEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseStop { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ValueTask StartAsync(CancellationToken token) => ValueTask.CompletedTask;
        public async ValueTask StopAsync(CancellationToken token) { StopEntered.TrySetResult(); await ReleaseStop.Task; }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

}
