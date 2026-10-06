using FgoPet.Extensibility;
using FgoPet.Plugin.Workspace;
using Xunit;

namespace FgoPet.Capability.Tests.Workspace;

public sealed class WorkspaceAuthorizationRegistryTests
{
    [Fact]
    public void Grants_require_current_host_scope_and_do_not_inherit_across_conversations()
    {
        var scope = new ToolScope("conversation", "role", null);
        var current = true;
        var registry = new WorkspaceAuthorizationRegistry(candidate => current && candidate == scope);
        Assert.Null(registry.GetCurrent(scope));
        var root = Path.GetTempPath();
        var first = registry.Grant(scope, root, root);
        Assert.Equal(first, registry.GetCurrent(scope));
        Assert.Null(registry.GetCurrent(scope with { ConversationId = "other" }));
        current = false;
        Assert.Null(registry.GetCurrent(scope));
        Assert.Throws<WorkspaceAccessException>(() => registry.Grant(scope, root, root));
        current = true;
        registry.Revoke(scope);
        Assert.Null(registry.GetCurrent(scope));
        var next = registry.Grant(scope, root, root);
        Assert.NotEqual(first.Authorization, next.Authorization);
        Assert.Throws<WorkspaceAccessException>(() => registry.Grant(scope, "relative", root));
    }

    [Fact]
    public void Full_registry_rejects_new_grants_without_evicting_existing_authority()
    {
        var registry = new WorkspaceAuthorizationRegistry(_ => true);
        var root = Path.GetTempPath();
        var scope = new ToolScope("conversation-0", "role", null);
        var first = registry.Grant(scope, root, root);
        for (var index = 1; index < 64; index++)
            registry.Grant(scope with { ConversationId = "conversation-" + index }, root, root);
        Assert.Throws<WorkspaceAccessException>(() => registry.Grant(scope with { ConversationId = "overflow" }, root, root));
        Assert.Equal(first, registry.GetCurrent(scope));
        registry.Revoke(scope);
        Assert.NotNull(registry.Grant(scope with { ConversationId = "overflow" }, root, root));
    }
}
