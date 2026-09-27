using FgoPet.Extensibility;
using FgoPet.Kernel.Companion;
using Xunit;
using static FgoPet.Foundation.Tests.PluginCatalogTests;

namespace FgoPet.Foundation.Tests;

public sealed class CompanionSignalPipelineTests
{
    [Fact]
    public async Task Only_active_sources_deliver_and_dispose_releases_subscriptions()
    {
        var source = new Source();
        var plugin = new SamplePlugin("test.activity", contributes: false)
        { Contributions = PluginContributions.Empty with { Signals = [source] } };
        var catalog = PluginCatalog.Create([plugin]);
        await using var runtime = new PluginRuntime(catalog);
        using var pipeline = new CompanionSignalPipeline(catalog, runtime);
        var deliveries = 0;
        pipeline.Signal += _ => deliveries++;
        source.Emit();
        Assert.Equal(0, deliveries);
        await runtime.StartAsync(default);
        source.Emit();
        Assert.Equal(1, deliveries);
        runtime.CloseAdmission();
        source.Emit();
        Assert.Equal(1, deliveries);
        pipeline.Dispose();
        Assert.Equal(0, source.Subscriptions);
    }

    [Fact]
    public async Task Subscriber_failure_is_isolated_and_records_only_a_fixed_code()
    {
        var source = new Source();
        var plugin = new SamplePlugin("test.activity", contributes: false)
        { Contributions = PluginContributions.Empty with { Signals = [source] } };
        var catalog = PluginCatalog.Create([plugin]);
        await using var runtime = new PluginRuntime(catalog);
        using var pipeline = new CompanionSignalPipeline(catalog, runtime);
        var delivered = false;
        pipeline.Signal += _ => throw new InvalidOperationException("private activity data");
        pipeline.Signal += _ => delivered = true;
        await runtime.StartAsync(default);
        source.Emit();
        Assert.True(delivered);
        Assert.Equal("COMPANION_SIGNAL_HANDLER_FAILED", pipeline.LastFailureCode);
    }

    private sealed class Source : ICompanionSignalSource
    {
        private Action<CompanionSignal>? _signal;
        public int Subscriptions { get; private set; }
        public event Action<CompanionSignal>? Signal
        {
            add { _signal += value; Subscriptions++; }
            remove { _signal -= value; Subscriptions--; }
        }
        public void Emit() => _signal?.Invoke(new(CompanionSignalKind.PhaseCompleted, "role", "activity",
            DateTimeOffset.UnixEpoch, 1, CompanionActivityPhase.Work, 60));
    }
}
