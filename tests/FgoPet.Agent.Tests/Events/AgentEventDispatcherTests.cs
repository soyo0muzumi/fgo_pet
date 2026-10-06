using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Text.Json;
using FgoPet.Extensibility;
using FgoPet.Kernel.Agent;
using Xunit;

namespace FgoPet.Agent.Tests.Events;

public sealed class AgentEventDispatcherTests
{
    private const string Run = "1234567890ABCDEF12345678";
    private const string Call = "ABCDEF1234567890ABCDEF12";
    private static readonly DateTimeOffset Timestamp = new(2026, 10, 6, 0, 0, 0, TimeSpan.Zero);
    private static ImmutableArray<AgentEvent> Events => [new(1, Timestamp, AgentEventKind.RunStarted, Run, 0),
        new(2, Timestamp.AddSeconds(1), AgentEventKind.ToolFailed, Run, 1, Call, "TOOL_UNAVAILABLE")];

    [Fact]
    public async Task Projection_preserves_order_and_contains_only_safe_metadata()
    {
        var a = new Observer("a");
        var b = new Observer("b");
        await using var host = await Host.Create(a, b);
        await host.Dispatcher.DispatchAsync(Events, default);
        foreach (var observer in new[] { a, b }) {
            Assert.Equal(new[] { 1L, 2L }, observer.Notifications.Select(n => n.Sequence));
            var second = observer.Notifications.Last();
            Assert.Equal(new AgentRunNotification(2, Timestamp.AddSeconds(1), "ToolFailed", Run, 1, Call, "TOOL_UNAVAILABLE"), second);
            var fields = JsonSerializer.SerializeToElement(second).EnumerateObject().Select(p => p.Name).Order().ToArray();
            Assert.Equal(new[] { "CallCorrelation", "ErrorCode", "Kind", "RunCorrelation", "Sequence", "StepNumber", "Timestamp" }, fields);
            Assert.DoesNotContain("private-fixture", JsonSerializer.Serialize(second));
        }
    }

    [Fact]
    public async Task Inactive_closed_admission_and_changed_captured_id_do_not_dispatch()
    {
        var observer = new Observer("fixture");
        await using var host = await Host.Create(false, null, observer);
        await host.Dispatcher.DispatchAsync(Events, default);
        Assert.Empty(observer.Notifications);
        Assert.True((await host.Runtime.StartAsync(default)).Succeeded);
        observer.Id = "changed";
        await host.Dispatcher.DispatchAsync(Events, default);
        Assert.Empty(observer.Notifications);
        observer.Id = "fixture";
        host.Runtime.CloseAdmission();
        await host.Dispatcher.DispatchAsync(Events, default);
        Assert.Empty(observer.Notifications);
    }

    [Fact]
    public async Task Admission_is_rechecked_after_every_callback()
    {
        var observer = new Observer("a");
        var later = new Observer("b");
        await using var host = await Host.Create(observer, later);
        observer.Handler = (_, _) => { host.Runtime.CloseAdmission(); return ValueTask.CompletedTask; };
        await host.Dispatcher.DispatchAsync(Events, default);
        Assert.Single(observer.Notifications);
        Assert.Empty(later.Notifications);
    }

    [Fact]
    public async Task Provider_id_is_rechecked_after_every_callback()
    {
        var observer = new Observer("fixture");
        await using var host = await Host.Create(observer);
        observer.Handler = (_, _) => { observer.Id = "changed"; return ValueTask.CompletedTask; };
        await host.Dispatcher.DispatchAsync(Events, default);
        Assert.Single(observer.Notifications);
    }

    [Fact]
    public async Task Sync_async_and_cancellation_exceptions_are_isolated_without_private_error_projection()
    {
        var sync = new Observer("sync") { Handler = (_, _) => throw new InvalidOperationException("private-fixture") };
        var asyncFailure = new Observer("async") { Handler = (_, _) => new(Task.FromException(new InvalidOperationException("private-fixture"))) };
        var canceled = new Observer("canceled") { Handler = (_, _) => throw new OperationCanceledException("private-fixture") };
        var healthy = new Observer("healthy");
        await using var host = await Host.Create(sync, asyncFailure, canceled, healthy);
        await host.Dispatcher.DispatchAsync(Events, default);
        Assert.All(new[] { sync, asyncFailure, canceled, healthy }, p => Assert.Equal(2, p.Notifications.Count));
        Assert.DoesNotContain("private-fixture", JsonSerializer.Serialize(healthy.Notifications));
    }

    [Fact]
    public async Task Ignored_cancellation_has_one_shared_batch_deadline_and_no_overlapping_late_delivery()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var observer = new Observer("blocked") { Handler = (_, _) => { entered.TrySetResult(); return new(release.Task); } };
        var later = new Observer("later");
        await using var host = await Host.Create(true, TimeSpan.FromMilliseconds(80), observer, later);
        try {
            var dispatch = host.Dispatcher.DispatchAsync(Events, default).AsTask();
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
            await dispatch.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.Single(observer.Notifications);
            Assert.Empty(later.Notifications);
            await host.Dispatcher.DispatchAsync(Events, default).AsTask().WaitAsync(TimeSpan.FromSeconds(2));
            Assert.Single(observer.Notifications);
            Assert.Equal(2, later.Notifications.Count);
            release.SetException(new InvalidOperationException("private-late-fixture"));
        }
        finally { release.TrySetResult(); }
    }

    [Fact]
    public async Task Synchronously_blocking_callback_is_bounded()
    {
        using var release = new ManualResetEventSlim();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var exited = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var observer = new Observer("blocked") { Handler = (_, _) => {
            entered.TrySetResult(); release.Wait(); exited.TrySetResult(); return ValueTask.CompletedTask;
        } };
        await using var host = await Host.Create(true, TimeSpan.FromMilliseconds(80), observer);
        try {
            var dispatch = host.Dispatcher.DispatchAsync(Events, default).AsTask();
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
            await dispatch.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.Single(observer.Notifications);
        }
        finally { release.Set(); await exited.Task.WaitAsync(TimeSpan.FromSeconds(2)); }
    }

    [Fact]
    public async Task Caller_cancellation_bounds_an_observer_that_ignores_it()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var observer = new Observer("blocked") { Handler = (_, _) => { entered.TrySetResult(); return new(release.Task); } };
        await using var host = await Host.Create(observer);
        using var cancellation = new CancellationTokenSource();
        try {
            var dispatch = host.Dispatcher.DispatchAsync(Events, cancellation.Token).AsTask();
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => dispatch.WaitAsync(TimeSpan.FromSeconds(2)));
            Assert.Single(observer.Notifications);
        }
        finally { release.TrySetResult(); }
    }

    [Fact]
    public async Task Pre_canceled_batch_does_not_dispatch_even_if_empty()
    {
        var observer = new Observer("fixture");
        await using var host = await Host.Create(observer);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await host.Dispatcher.DispatchAsync([], cancellation.Token));
        Assert.Empty(observer.Notifications);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(10001)]
    public async Task Timeout_must_be_positive_and_at_most_ten_seconds(int milliseconds)
    {
        await using var host = await Host.Create(new Observer("fixture"));
        Assert.Throws<ArgumentOutOfRangeException>(() => new AgentEventDispatcher(host.Catalog, host.Runtime, TimeSpan.FromMilliseconds(milliseconds)));
    }

    [Fact]
    public async Task Unsafe_correlation_and_error_strings_are_not_forwarded_as_metadata()
    {
        var observer = new Observer("fixture");
        await using var host = await Host.Create(observer);
        ImmutableArray<AgentEvent> unsafeEvents = [Events[0] with { RunCorrelation = "private-fixture" },
            Events[1] with { CallCorrelation = "D:/private-fixture" }, Events[1] with { ErrorCode = "private-fixture" },
            Events[0] with { Kind = (AgentEventKind)999 }];
        await host.Dispatcher.DispatchAsync(unsafeEvents, default);
        Assert.Empty(observer.Notifications);
        await host.Dispatcher.DispatchAsync(Events, default);
        Assert.Equal(2, observer.Notifications.Count);
    }

    private sealed class Observer(string id) : IAgentRunObserver
    {
        public string Id { get; set; } = id;
        public ConcurrentQueue<AgentRunNotification> Notifications { get; } = new();
        public Func<AgentRunNotification, CancellationToken, ValueTask>? Handler { get; set; }
        public ValueTask ObserveAsync(AgentRunNotification notification, CancellationToken token) {
            Notifications.Enqueue(notification);
            return Handler?.Invoke(notification, token) ?? ValueTask.CompletedTask;
        }
    }
    private sealed class Plugin(params Observer[] observers) : IFgoPetPlugin
    {
        public PluginManifest Manifest { get; } = new("firstparty.fixture", "1.0.0", 1, []);
        public PluginContributions Contributions { get; } = PluginContributions.Empty with { AgentObservers = [.. observers] };
        public ValueTask StartAsync(CancellationToken token) => ValueTask.CompletedTask;
        public ValueTask StopAsync(CancellationToken token) => ValueTask.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
    private sealed class Host(PluginCatalog catalog, PluginRuntime runtime, AgentEventDispatcher dispatcher) : IAsyncDisposable
    {
        public PluginCatalog Catalog { get; } = catalog;
        public PluginRuntime Runtime { get; } = runtime;
        public AgentEventDispatcher Dispatcher { get; } = dispatcher;
        public static Task<Host> Create(params Observer[] observers) => Create(true, null, observers);
        public static async Task<Host> Create(bool start, TimeSpan? timeout, params Observer[] observers) {
            var catalog = PluginCatalog.Create([new Plugin(observers)]);
            var runtime = new PluginRuntime(catalog);
            if (start) Assert.True((await runtime.StartAsync(default)).Succeeded);
            return new(catalog, runtime, new(catalog, runtime, timeout));
        }
        public ValueTask DisposeAsync() => Runtime.DisposeAsync();
    }
}
