using FgoPet.App.Runtime;
using FgoPet.Core.Portraits;
using FgoPet.Extensibility;
using FgoPet.Kernel.Companion;
using FgoPet.Kernel.Lifecycle;
using Xunit;
using static FgoPet.Foundation.Tests.PluginCatalogTests;

namespace FgoPet.Foundation.Tests;

public sealed class CompanionReactionPipelineTests
{
    [Fact]
    public async Task Role_change_cancels_pending_content_and_rejects_late_feedback()
    {
        using var process = new ProcessLifetime();
        var operations = new BackgroundOperationTracker(process);
        var source = new Source();
        var provider = new Provider();
        var plugin = new SamplePlugin("test.content", contributes: false)
        { Contributions = PluginContributions.Empty with { Signals = [source], FeedbackProviders = [provider] } };
        var catalog = PluginCatalog.Create([plugin]);
        await using var runtime = new PluginRuntime(catalog);
        using var signals = new CompanionSignalPipeline(catalog, runtime);
        var identity = new AppRuntime();
        using var presentation = new CompanionPresentation(identity);
        using var reactions = new CompanionReactionPipeline(catalog, runtime, signals, identity, presentation, operations, process);
        await runtime.StartAsync(process.StoppingToken);
        identity.SetActiveRole(new("a", "casual", "1.0.0", "role"));
        source.Emit("role");
        await provider.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        identity.SetActiveRole(new("b", "casual", "1.0.0", "role"));
        Assert.True(provider.Token.IsCancellationRequested);
        provider.Release.TrySetResult(new("old package", ExpressionSemantic.Happy));
        process.RequestStop();
        await operations.DrainAsync().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Null(presentation.Current);
    }

    [Fact]
    public async Task Presentation_rejects_stale_identity_and_clears_after_role_change()
    {
        var identity = new AppRuntime();
        var first = new ActiveRoleState("a", "casual", "1.0.0", "role");
        identity.SetActiveRole(first);
        using var presentation = new CompanionPresentation(identity);
        Assert.True(presentation.Apply(first, ExpressionSemantic.Happy, "current"));
        Assert.Equal("current", presentation.Current!.Text);
        identity.SetActiveRole(new("b", "casual", "1.0.0", "role"));
        Assert.Null(presentation.Current);
        Assert.False(presentation.Apply(first, ExpressionSemantic.Angry, "stale"));
        await Task.CompletedTask;
    }

    private sealed class Source : ICompanionSignalSource
    {
        public event Action<CompanionSignal>? Signal;
        public void Emit(string role) => Signal?.Invoke(new(CompanionSignalKind.ActivityStarted, role, "activity",
            DateTimeOffset.UnixEpoch, 1, CompanionActivityPhase.Work, 0));
    }
    private sealed class Provider : ICompanionFeedbackProvider
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<CompanionFeedback?> Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public CancellationToken Token { get; private set; }
        public Task<CompanionFeedback?> GetFeedbackAsync(CompanionSignal signal, CompanionContentScope scope, CancellationToken token)
        {
            Token = token;
            Entered.TrySetResult();
            return Release.Task;
        }
    }
}
