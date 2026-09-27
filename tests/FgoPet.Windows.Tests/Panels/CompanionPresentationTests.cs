using System.Windows.Threading;
using FgoPet.App.Panels;
using FgoPet.App.Runtime;
using FgoPet.Core.Portraits;
using FgoPet.Kernel.Companion;
using Xunit;

namespace FgoPet.Windows.Tests.Panels;

public sealed class CompanionPresentationTests
{
    [Fact]
    public Task Queued_content_from_the_previous_role_or_disposed_surface_is_discarded() => StaRunner.RunAsync(async () =>
    {
        var identity = new AppRuntime();
        using var presentation = new CompanionPresentation(identity);
        using var panel = new AttachedPanelViewModel(TimeProvider.System, null, runtime: identity, presentation: presentation);
        var first = new ActiveRoleState("a", "casual", "1.0.0", "role");
        identity.SetActiveRole(first);
        presentation.Apply(first, ExpressionSemantic.Happy, "old role");
        var current = new ActiveRoleState("b", "casual", "1.0.0", "role");
        identity.SetActiveRole(current);
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        Assert.Empty(panel.Dialogue);
        presentation.Apply(current, ExpressionSemantic.Happy, "current role");
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        Assert.Single(panel.Dialogue);
        presentation.Apply(current, ExpressionSemantic.Neutral, "after disposal");
        panel.Dispose();
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        Assert.Single(panel.Dialogue);
    });
}
