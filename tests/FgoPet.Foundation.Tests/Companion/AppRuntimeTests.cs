using FgoPet.App.Runtime;
using Xunit;

namespace FgoPet.App.Tests.Runtime;

public sealed class AppRuntimeTests
{
    [Fact]
    public void Setting_active_role_publishes_the_new_snapshot()
    {
        var runtime = new AppRuntime();
        ActiveRoleState? published = null;
        runtime.ActiveRoleChanged += (_, args) => published = args.State;
        var state = new ActiveRoleState("pack", "casual", "1.0.0", "mash_kyrielight");

        runtime.SetActiveRole(state);

        Assert.Equal(state, runtime.ActiveRole);
        Assert.Equal(state, published);
    }
}
