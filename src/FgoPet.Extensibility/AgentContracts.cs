using System.Collections.Immutable;

namespace FgoPet.Extensibility;

/// <summary>Host-created execution correlation; model arguments cannot grant these values.</summary>
public sealed record ToolExecutionContext(string RunId, int StepNumber, string CallId, string IdempotencyKey)
{
    public ToolResourceAuthorization? ResourceAuthorization { get; init; }
    public ToolBusinessConfirmation? BusinessConfirmation { get; init; }
}

/// <summary>Owner-frozen business draft. Model arguments and generic policy cannot manufacture confirmation.</summary>
public sealed record ToolBusinessConfirmation(string DraftId, int Version, string Fingerprint);
public interface IToolBusinessConfirmationProvider
{
    ToolBusinessConfirmation PrepareConfirmation(ToolInvocation invocation, CancellationToken token);
    void ValidateConfirmation(ToolInvocation invocation, ToolBusinessConfirmation confirmation, CancellationToken token);
}
public sealed class ToolBusinessConfirmationException(string code) : Exception(code)
{
    public string Code { get; } = code;
}

/// <summary>Owner evidence about effects, independently of authorization.</summary>
public enum ToolExecutionState { NotExecuted, Committed, Unknown }

public sealed record SkillDescriptor(string Id, string Description, ImmutableArray<string> RequiredTools,
    ImmutableArray<string> OptionalTools, string Version, string ContentDigest);
public sealed record SkillContent(SkillDescriptor Descriptor, string Instructions);
public interface ISkillProvider
{
    ImmutableArray<SkillDescriptor> Catalog { get; }
    IReadOnlyList<SkillDescriptor> ListSkills(ToolScope scope) => Catalog;
    ValueTask<SkillContent?> LoadAsync(ToolScope scope, string skillId, CancellationToken token);
}

public enum AgentContextKind { Data, Instruction }
/// <summary>Source is a resource ID under the registered plugin, never an authority or a local path.</summary>
public sealed record AgentContextBlock(string Source, AgentContextKind Kind, string Content, int Priority,
    int? BudgetHint = null);
public sealed record ProtectedQueryReference(string UserMessageId, string Fingerprint);
/// <summary>Trusted host resolves a validated original user message privately; the reference carries no query text.</summary>
public interface IAgentQueryResolver
{
    ValueTask<string?> ResolveAsync(ToolScope scope, ProtectedQueryReference reference, CancellationToken token);
}
public sealed record AgentContextRequest(ToolScope Scope, string RunId, int StepNumber,
    ProtectedQueryReference Query, int InputTokenBudget, ImmutableArray<ToolDescriptor> AvailableTools);
public interface IAgentContextProvider
{
    string Id { get; }
    bool ProvidesTrustedInstructions => false;
    ValueTask<IReadOnlyList<AgentContextBlock>> BuildAsync(AgentContextRequest request, CancellationToken token);
}

/// <summary>Sanitized notification projection; excludes every content-bearing request/response DTO.</summary>
public sealed record AgentRunNotification(long Sequence, DateTimeOffset Timestamp, string Kind,
    string RunCorrelation, int StepNumber, string? CallCorrelation = null, string? ErrorCode = null);
public interface IAgentRunObserver
{
    string Id { get; }
    ValueTask ObserveAsync(AgentRunNotification notification, CancellationToken token);
}
