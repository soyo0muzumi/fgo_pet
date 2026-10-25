using System.Collections.Immutable;
using FgoPet.Core.Dialogue;
using FgoPet.Extensibility;

namespace FgoPet.Kernel.Agent;

public enum ContextDiagnosticStage
{
    CapacityResolved, BeforeCompaction, AfterCompaction, CompactionStarted,
    CompactionSkipped, CompactionCommitted, Rejected, CompactionApplied
}
public enum ContextCompactionKind { None, ConversationProjection, RunProjection }
public enum ContextRejectionPoint
{
    None, ProviderCount, InitialInputBudget, MandatoryBlock, DeferredBlockBytes,
    FinalAssemblyInputBudget, NoTruncatableData, FinalStepInputBudget
}
public enum ContextCompactionReason
{
    None, TokenThreshold, ByteThreshold, Caller, NotTriggered, NoEligibleHistory,
    BatchInputLimit, AuxiliaryCallLimit, InvalidSummary, NoReduction,
    FinalProjectionLimit, ProjectionChanged, Failure, Cancelled
}

/// <summary>Operational counts only. No content, identifiers, schemas, routes or exception messages.</summary>
public sealed record ContextBudgetDiagnostic(ContextDiagnosticStage Stage)
{
    public ContextRejectionPoint Point { get; init; }
    public ContextCompactionReason Decision { get; init; }
    public ContextCompactionKind CompactionKind { get; init; }
    public ContextLimitSource? CapacitySource { get; init; }
    public int? WindowTokens { get; init; }
    public int? InputBudget { get; init; }
    public int? OutputTokens { get; init; }
    public int? TriggerTokens { get; init; }
    public int? MeasuredInput { get; init; }
    public int? MessageCount { get; init; }
    public int? ToolCount { get; init; }
    public int? ToolDefinitionsInput { get; init; }
    public int? SystemInput { get; init; }
    public int? TranscriptInput { get; init; }
    public int? MandatoryLowerBoundInput { get; init; }
    public int? BlockUtf8Bytes { get; init; }
    public int? EligibleGroups { get; init; }
    public int? AuxiliaryBudgetReservations { get; init; }
    public int? Step { get; init; }
}

internal static class ContextBudgetDiagnostics
{
    internal static void Emit(Action<ContextBudgetDiagnostic>? observer, ContextBudgetDiagnostic value)
    {
        try { observer?.Invoke(value); }
        catch (Exception) { /* Diagnostics never change request admission, cancellation or execution. */ }
    }

    internal static void Observe(Action<ContextBudgetDiagnostic>? observer, ContextBudgetDiagnostic value,
        StepEnvironment environment, Func<StepEnvironment, int> measure)
    {
        if (observer is null) return;
        try
        {
            var messages = environment.Messages.IsDefault ? ImmutableArray<ModelMessage>.Empty : environment.Messages;
            var tools = environment.Tools.IsDefault ? ImmutableArray<ToolDescriptor>.Empty : environment.Tools;
            var systems = messages.Where(message => message.Role == ModelMessageRole.System).ToImmutableArray();
            var transcript = messages.Where(message => message.Role != ModelMessageRole.System).ToImmutableArray();
            var latestUser = messages.LastOrDefault(message => message.Role == ModelMessageRole.User);
            var mandatory = latestUser is null ? systems : systems.Add(latestUser);
            int? Count(ImmutableArray<ModelMessage> subset, ImmutableArray<ToolDescriptor> definitions)
            {
                try
                {
                    if (subset.IsEmpty) subset = [new(ModelMessageRole.System, string.Empty, [])];
                    var count = measure(new(subset, definitions) { Kind = environment.Kind });
                    return count >= 0 ? count : null;
                }
                catch (Exception) { return null; }
            }
            Emit(observer, value with {
                MeasuredInput = value.MeasuredInput ?? Count(messages, tools),
                MessageCount = messages.Length, ToolCount = tools.Length,
                ToolDefinitionsInput = tools.IsEmpty ? 0 : Count([], tools),
                SystemInput = systems.IsEmpty ? 0 : Count(systems, []),
                TranscriptInput = transcript.IsEmpty ? 0 : Count(transcript, []),
                MandatoryLowerBoundInput = Count(mandatory, tools)
            });
        }
        catch (Exception) { /* Invalid measurements are unknown; never expose their exception text. */ }
    }
}
