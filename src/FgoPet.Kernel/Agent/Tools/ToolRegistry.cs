using System.Collections.Immutable;
using FgoPet.Extensibility;

namespace FgoPet.Kernel.Agent;

/// <summary>Execution view of the host catalog. Activation remains owned by PluginRuntime.</summary>
public sealed class ToolRegistry(PluginCatalog catalog, PluginRuntime runtime)
{
    public ImmutableArray<ToolDescriptor> GetTools(ToolScope scope)
        => IsValidScope(scope) ? catalog.Tools.Where(tool => runtime.IsActive(tool.PluginId))
            .Select(tool => tool.Descriptor).ToImmutableArray() : [];

    public bool TryResolve(string name, ToolScope scope, out RegisteredTool tool)
    {
        tool = null!;
        if (!IsValidScope(scope)) return false;
        foreach (var candidate in catalog.Tools)
        {
            if (candidate.Descriptor.Name == name && runtime.IsActive(candidate.PluginId))
            {
                tool = candidate;
                return true;
            }
        }
        return false;
    }

    internal bool IsCurrent(RegisteredTool tool, ToolScope scope)
        => TryResolve(tool.Descriptor.Name, scope, out var current) && ReferenceEquals(tool, current);

    internal string GetPluginVersion(RegisteredTool tool)
        => catalog.Plugins.Single(plugin => plugin.Manifest.Id == tool.PluginId).Manifest.Version;

    private static bool IsValidScope(ToolScope? scope)
        => scope is not null && !string.IsNullOrWhiteSpace(scope.ConversationId)
            && !string.IsNullOrWhiteSpace(scope.RoleId)
            && (scope.ProjectId is null || !string.IsNullOrWhiteSpace(scope.ProjectId));
}
