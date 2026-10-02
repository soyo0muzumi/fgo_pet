using System.Collections.Immutable;
using FgoPet.Extensibility;

namespace FgoPet.Kernel.Conversation;

/// <summary>Routes only active, declared contributions. It owns no capability's domain or confirmation state.</summary>
public sealed class ConversationCapabilityRouter(PluginCatalog catalog, PluginRuntime runtime)
{
    public string? LastFailureCode { get; private set; }
    public IReadOnlyList<ConversationPromptBlock> BuildPrompt(ToolScope scope, string message, bool toolsAvailable)
    {
        var blocks = new List<ConversationPromptBlock>();
        foreach (var entry in catalog.Prompts.Where(entry => runtime.IsActive(entry.PluginId)))
        {
            try
            {
                var supplied = entry.Provider.BuildPrompt(scope, message, toolsAvailable);
                if (supplied.Count > 16 || supplied.Any(block => block is null || string.IsNullOrWhiteSpace(block.Source)
                    || block.Source.Length > 256 || block.Text is null || block.Text.Length > ConversationContributionLimits.ReplyChars
                    || !Enum.IsDefined(block.Kind)))
                    throw new InvalidOperationException();
                blocks.AddRange(supplied);
            }
            catch (Exception) { LastFailureCode = "CONVERSATION_CONTEXT_PROVIDER_FAILED"; }
        }
        return blocks;
    }

    public void ObserveCompletedTurn(ConversationCompletedTurn turn)
    {
        foreach (var entry in catalog.PostTurnObservers.Where(entry => runtime.IsActive(entry.PluginId)))
        {
            turn.CancellationToken.ThrowIfCancellationRequested();
            try { entry.Provider.Observe(turn); }
            catch (OperationCanceledException) when (turn.CancellationToken.IsCancellationRequested) { throw; }
            catch (Exception) { LastFailureCode = "POST_TURN_OBSERVER_FAILED"; }
        }
    }
    public ImmutableArray<ToolDescriptor> Tools => catalog.Tools
        .Where(tool => runtime.IsActive(tool.PluginId) && tool.Descriptor.Effect != ToolEffect.Command)
        .Select(tool => tool.Descriptor).ToImmutableArray();

    public async Task<ToolResult> InvokeAsync(string name, ToolInvocation invocation, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var registered = catalog.Tools.FirstOrDefault(tool => tool.Descriptor.Name == name && runtime.IsActive(tool.PluginId));
        if (registered is null) return new(false, default, "TOOL_UNAVAILABLE");
        if (registered.Descriptor.Effect == ToolEffect.Command) return new(false, default, "TOOL_REQUIRES_AUTHORIZATION");
        try
        {
            var result = await registered.Provider.InvokeAsync(invocation, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            return result;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception) { return new(false, default, "TOOL_PROVIDER_FAILED"); }
    }

    public string BuildContext(ToolScope scope, string message)
    {
        var context = new System.Text.StringBuilder();
        foreach (var entry in catalog.Contexts.Where(entry => runtime.IsActive(entry.PluginId)))
        {
            var text = entry.Provider.BuildContext(scope, message);
            if (string.IsNullOrWhiteSpace(text)) continue;
            var remaining = ConversationContributionLimits.RuntimeContextChars - context.Length;
            if (context.Length > 0) remaining--;
            if (remaining <= 0) break;
            var length = Math.Min(remaining, text.Length);
            if (length < text.Length && char.IsHighSurrogate(text[length - 1])) length--;
            if (length == 0) break;
            if (context.Length > 0) context.Append('\n');
            context.Append(text.AsSpan(0, length));
        }
        return context.ToString();
    }

    public string? GetPromptState(ToolScope scope)
    {
        var states = catalog.Continuations.Where(entry => runtime.IsActive(entry.PluginId))
            .Select(entry => entry.Provider.GetPromptState(scope)).Where(text => !string.IsNullOrWhiteSpace(text)).ToArray();
        return states.Length == 0 ? null : string.Join('\n', states);
    }

    public ConversationContributionResult? TryHandleInput(ToolScope scope, string message)
    {
        // Never authorize the first of several unrelated pending drafts by registry order.
        var candidates = catalog.Continuations.Where(entry => runtime.IsActive(entry.PluginId)
            && entry.Provider.GetPromptState(scope) is not null).ToArray();
        if (candidates.Length > 1)
            return new(CapabilityOutcome.ConfirmationUnknown, "当前有多份待确认草稿，请先说明要处理哪一份。");
        return candidates.Length == 1 ? candidates[0].Provider.TryHandleInput(scope, message) : null;
    }

    public ConversationContributionResult? TryInterpretReply(ToolScope scope, string reply)
    {
        foreach (var interpreter in catalog.TextInterpreters.Where(entry => runtime.IsActive(entry.PluginId)))
        {
            var result = interpreter.Provider.TryInterpretReply(scope, reply);
            if (result is not null) return result;
        }
        return null;
    }

    public void ClearRole(string roleId)
    {
        foreach (var entry in catalog.Continuations) entry.Provider.ClearRole(roleId);
    }

    public void RemoveConversation(ToolScope scope)
    {
        foreach (var entry in catalog.Continuations) entry.Provider.RemoveConversation(scope);
    }
}
