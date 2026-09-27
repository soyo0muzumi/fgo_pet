using System.Collections.Concurrent;
using FgoPet.Extensibility;
using FgoPet.Kernel.Lifecycle;
using Xunit;

namespace FgoPet.Foundation.Tests;

public sealed class ProcessLifetimeTests
{
    [Fact]
    public async Task Normal_shutdown_cancels_drains_releases_and_exits_once()
    {
        using var lifetime = new ProcessLifetime();
        var tracker = new BackgroundOperationTracker(lifetime);
        var events = new ConcurrentQueue<string>();
        var work = tracker.RunAsync("CHAT_TURN", async token =>
        {
            try { await Task.Delay(Timeout.InfiniteTimeSpan, token); }
            finally { events.Enqueue("drained"); }
        });
        var coordinator = new ShutdownCoordinator(lifetime, tracker,
            [new Participant("MEMORY_QUEUE", () => { events.Enqueue("stopped"); return Task.CompletedTask; })],
            () => { events.Enqueue("released"); return ValueTask.CompletedTask; }, () => events.Enqueue("exit"));
        var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => coordinator.StopAsync()));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => work);
        Assert.Equal(new[] { "drained", "stopped", "released", "exit" }, events);
        Assert.All(results, result => Assert.Empty(result.Errors));
    }

    [Fact]
    public async Task Admission_after_stop_is_rejected_before_calling_the_operation()
    {
        using var lifetime = new ProcessLifetime();
        var tracker = new BackgroundOperationTracker(lifetime);
        lifetime.RequestStop();
        var invoked = false;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => tracker.RunAsync("LATE_WORK", _ => { invoked = true; return Task.CompletedTask; }));
        Assert.False(invoked);
        Assert.True(lifetime.StoppingToken.IsCancellationRequested);
    }

    [Fact]
    public async Task A_failed_participant_does_not_block_release_or_exit_and_diagnostics_are_safe()
    {
        using var lifetime = new ProcessLifetime();
        var events = new List<string>();
        var coordinator = new ShutdownCoordinator(lifetime, new(lifetime),
            [new Participant("FAILED_PARTICIPANT", () => throw new InvalidOperationException("secret user text")),
             new Participant("OTHER_PARTICIPANT", () => { events.Add("other"); return Task.CompletedTask; })],
            () => { events.Add("release"); return ValueTask.CompletedTask; }, () => events.Add("exit"));
        var result = await coordinator.StopAsync();
        Assert.Equal("SHUTDOWN_PARTICIPANT_FAILED", Assert.Single(result.Errors).Code);
        Assert.Equal(new[] { "other", "release", "exit" }, events);
        Assert.DoesNotContain("secret", result.ToString());
    }

    [Fact]
    public async Task Failed_background_operation_records_a_code_and_does_not_poison_shutdown()
    {
        using var lifetime = new ProcessLifetime();
        var tracker = new BackgroundOperationTracker(lifetime);
        await Assert.ThrowsAsync<InvalidOperationException>(() => tracker.RunAsync("MODEL_CALL", _ => throw new InvalidOperationException("secret prompt")));
        Assert.Equal("BACKGROUND_OPERATION_FAILED", Assert.Single(tracker.Failures).Code);
        Assert.DoesNotContain("secret", string.Join(',', tracker.Failures));
        lifetime.RequestStop();
        await tracker.DrainAsync();
    }

    [Fact]
    public async Task Owner_leases_drain_before_plugins_are_released_but_new_capabilities_are_already_fenced()
    {
        using var lifetime = new ProcessLifetime();
        var plugin = new PluginCatalogTests.SamplePlugin("test.one", contributes: false);
        var extensions = new FgoPet.Extensibility.PluginRuntime(FgoPet.Extensibility.PluginCatalog.Create([plugin]));
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var drained = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var coordinator = new ShutdownCoordinator(lifetime, new(lifetime),
            [new Participant("OWNER_LEASES", async () => { entered.SetResult(); await drained.Task; })],
            () => ValueTask.CompletedTask, () => { }, extensions);
        await coordinator.StartExtensionsAsync();
        var stop = coordinator.StopAsync();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        try
        {
            Assert.False(extensions.IsActive("test.one"));
            Assert.DoesNotContain("dispose", plugin.Events);
        }
        finally { drained.TrySetResult(); await stop; }
        Assert.Contains("dispose", plugin.Events);
    }

    [Fact]
    public async Task Plugin_lifecycle_is_stopped_before_host_release()
    {
        using var lifetime = new ProcessLifetime();
        var plugin = new PluginCatalogTests.SamplePlugin("test.one", contributes: false);
        var extensions = new PluginRuntime(PluginCatalog.Create([plugin]));
        var coordinator = new ShutdownCoordinator(lifetime, new(lifetime), [],
            () => { Assert.Contains("dispose", plugin.Events); return ValueTask.CompletedTask; }, () => { }, extensions);
        Assert.True((await coordinator.StartExtensionsAsync()).Succeeded);
        await coordinator.StopAsync();
        Assert.Equal(new[] { "start", "stop", "dispose" }, plugin.Events);
    }

    [Fact]
    public async Task Cancellation_callback_and_release_failures_are_reported_without_stranding_exit()
    {
        using var lifetime = new ProcessLifetime();
        using var registration = lifetime.StoppingToken.Register(() => throw new InvalidOperationException("private content"));
        var exited = false;
        var coordinator = new ShutdownCoordinator(lifetime, new(lifetime), [],
            () => throw new InvalidOperationException("private path"), () => exited = true);
        var result = await coordinator.StopAsync().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(exited);
        Assert.Equal(new[] { "PROCESS_CANCELLATION_CALLBACK_FAILED", "HOST_RELEASE_FAILED" }, result.Errors.Select(e => e.Code));
        Assert.DoesNotContain("private", result.ToString());
    }

    [Fact]
    public async Task Disposal_during_an_active_cancellation_callback_does_not_race_the_token_source()
    {
        var lifetime = new ProcessLifetime();
        using var entered = new ManualResetEventSlim();
        using var resume = new ManualResetEventSlim();
        using var registration = lifetime.StoppingToken.Register(() =>
        {
            entered.Set();
            if (!resume.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException();
        });
        var stopping = Task.Run(lifetime.RequestStop);
        try
        {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));
            lifetime.Dispose();
        }
        finally { resume.Set(); }
        await stopping.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(lifetime.CancellationCallbackFailed);
        Assert.True(lifetime.StoppingToken.IsCancellationRequested);
        lifetime.Dispose();
    }

    private sealed class Participant(string code, Func<Task> stop) : IShutdownParticipant
    {
        public string Code => code;
        public Task StopAsync(CancellationToken cancellationToken) => stop();
    }
}
