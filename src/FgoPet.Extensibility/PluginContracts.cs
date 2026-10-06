using System.Collections.Immutable;
using System.Text.Json;

namespace FgoPet.Extensibility;

public sealed record PluginManifest(string Id, string Version, int ApiVersion, ImmutableArray<string> Dependencies);
public sealed record WorkspaceDescriptor(string Id, string Title);
public enum TransientAnchor { Portrait, Launcher }
public sealed record TransientSurfaceDescriptor(string Id, string Title, double PreferredWidthDip,
    double PreferredHeightDip, TransientAnchor Anchor = TransientAnchor.Launcher);
public sealed record SettingsPageDescriptor(string Id, string Title)
{
    public string Description { get; init; } = string.Empty;
    public string Group { get; init; } = string.Empty;
    public ImmutableArray<string> Keywords { get; init; } = [];
    public int Order { get; init; }
}
public sealed record PluginContributions(ImmutableArray<IToolProvider> Tools,
    ImmutableArray<WorkspaceDescriptor> Workspaces, ImmutableArray<SettingsPageDescriptor> SettingsPages)
{
    public static PluginContributions Empty { get; } = new([], [], []);
    public ImmutableArray<IConversationContextProvider> Contexts { get; init; } = [];
    public ImmutableArray<IConversationContinuationProvider> Continuations { get; init; } = [];
    public ImmutableArray<ITextReplyInterpreter> TextInterpreters { get; init; } = [];
    public ImmutableArray<ICompanionSignalSource> Signals { get; init; } = [];
    public ImmutableArray<IConversationPromptProvider> Prompts { get; init; } = [];
    public ImmutableArray<IPostTurnObserver> PostTurnObservers { get; init; } = [];
    public ImmutableArray<ICompanionFeedbackProvider> FeedbackProviders { get; init; } = [];
    public ImmutableArray<TransientSurfaceDescriptor> TransientSurfaces { get; init; } = [];
    public ImmutableArray<ISkillProvider> Skills { get; init; } = [];
    public ImmutableArray<IAgentContextProvider> AgentContexts { get; init; } = [];
    public ImmutableArray<IAgentRunObserver> AgentObservers { get; init; } = [];
}

/// <summary>Trusted, statically registered capability. The supplied token represents process shutdown.</summary>
public interface IFgoPetPlugin : IAsyncDisposable
{
    PluginManifest Manifest { get; }
    PluginContributions Contributions { get; }
    ValueTask StartAsync(CancellationToken stoppingToken);
    ValueTask StopAsync(CancellationToken cancellationToken);
}

public enum ToolEffect { ReadOnly, Proposal, Command }
public sealed record ToolScope(string ConversationId, string RoleId, string? ProjectId);
public sealed record ToolInvocation(ToolScope Scope, JsonElement Arguments)
{
    public ToolExecutionContext? ExecutionContext { get; init; }
}
public sealed record ToolResult(bool Success, JsonElement Payload, string? ErrorCode = null)
{
    public ConversationContributionResult? Conversation { get; init; }
    public ToolExecutionState? ExecutionState { get; init; }
}

public sealed record ToolDescriptor
{
    public ToolDescriptor(string name, string description, string parametersJson, ToolEffect effect)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Length > 64 || string.IsNullOrWhiteSpace(description) || description.Length > 2048)
            throw new ArgumentException("Invalid tool descriptor.");
        if (!Enum.IsDefined(effect)) throw new ArgumentOutOfRangeException(nameof(effect));
        if (parametersJson is null || parametersJson.Length > 65536) throw new ArgumentException("Invalid tool schema.");
        using var document = JsonDocument.Parse(parametersJson);
        if (document.RootElement.ValueKind != JsonValueKind.Object) throw new ArgumentException("Tool schema must be an object.");
        Name = name; Description = description; Parameters = document.RootElement.Clone(); Effect = effect;
    }
    public string Name { get; }
    public string Description { get; }
    public JsonElement Parameters { get; }
    public ToolEffect Effect { get; }
}

public interface IToolProvider
{
    ToolDescriptor Descriptor { get; }
    ValueTask<ToolResult> InvokeAsync(ToolInvocation invocation, CancellationToken cancellationToken);
}
