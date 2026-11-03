using System.Collections.Immutable;
using FgoPet.Extensibility;

namespace FgoPet.Kernel.Agent;

/// <summary>Execution view of the host catalog. Activation remains owned by PluginRuntime.</summary>
public sealed class ToolRegistry(PluginCatalog catalog, PluginRuntime runtime)
{
    public ImmutableArray<ToolDescriptor> GetTools(ToolScope scope)
        => IsValidScope(scope) ? catalog.Tools.Where(tool => runtime.IsActive(tool.PluginId))
            .Select(DescribeCurrentAvailability).ToImmutableArray() : [];

    private static ToolDescriptor DescribeCurrentAvailability(RegisteredTool tool)
    {
        var descriptor = tool.Descriptor;
        if (tool.Provider is not IToolAvailabilityProvider provider) return descriptor;
        ToolAvailability availability;
        try { availability = provider.GetAvailability(); }
        catch (Exception) { availability = new(false, "TOOL_STATUS_UNAVAILABLE"); }
        if (availability is { IsAvailable: true }) return descriptor;

        // Providers may fail while loading local state. Never expose their exceptions, paths,
        // configuration values, or arbitrary text through a model-visible description.
        var code = availability?.ErrorCode;
        if (string.IsNullOrEmpty(code) || code.Length > 64
            || code.Any(character => character is not (>= 'A' and <= 'Z' or >= '0' and <= '9' or '_')))
            code = "TOOL_NOT_READY";
        var recovery = code switch
        {
            "BROWSER_VISUAL_NOT_ENABLED" => "Upgrade the browser grant in Web access settings to include image reading.",
            "DOCUMENT_PARSER_UNAVAILABLE" or "WEB_SEARCH_RUNTIME_MISSING" or "BROWSER_DEPENDENCY_MISSING"
                => "A required application component is missing; explain that the app package needs repair or replacement.",
            _ => "Explain the reported state and direct the user to this capability's settings or permissions.",
        };
        var prefix = $"Currently unavailable ({code}). {recovery} Do not call or retry until the setup changes. ";
        var description = descriptor.Description;
        if (prefix.Length + description.Length > 2048) description = description[..(2048 - prefix.Length)];
        return new(descriptor.Name, prefix + description, descriptor.Parameters.GetRawText(), descriptor.Effect);
    }

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
