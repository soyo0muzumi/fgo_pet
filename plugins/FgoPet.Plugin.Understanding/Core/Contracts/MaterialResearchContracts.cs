using FgoPet.Core.Dialogue;
using FgoPet.Core.Settings;
using FgoPet.Extensibility;

namespace FgoPet.Understanding.Contracts;

public sealed record MaterialSearchResult(string Title, string Url, string Snippet);

/// <summary>A single bounded search capability, never an open tool registry or page reader.</summary>
public interface IMaterialResearchSource
{
    Task<IReadOnlyList<MaterialSearchResult>> SearchAsync(ToolScope scope, string query, CancellationToken token, string? activityId = null);
}

public sealed record MaterialResearchRequest(string ActivityId, ToolScope Scope, ModelConnectionSettings Connection,
    string TopicKey, int TopicRevision, string Topic, TopicMaterialScope Kind);
public sealed record MaterialResearchDraft(int SourceIndex, string Summary, string RelevanceReason);
public interface IMaterialResearchModel
{
    Task<string?> GenerateQueryAsync(MaterialResearchRequest request, IChatDispatchGate dispatch,
        Action<CancellationToken> ensureCurrent, CancellationToken token);
    Task<MaterialResearchDraft?> SummarizeAsync(MaterialResearchRequest request, IReadOnlyList<MaterialSearchResult> results,
        IChatDispatchGate dispatch, Action<CancellationToken> ensureCurrent, CancellationToken token);
}

/// <summary>Independent host grant for sending effective interests to the named model and search provider.</summary>
public sealed record MaterialResearchConfiguration(ToolScope Scope, ModelConnectionSettings Connection,
    string Revision, int DailyLimit = 6);
public interface IMaterialResearchHost
{
    MaterialResearchConfiguration? Capture();
    bool TryCommit(MaterialResearchConfiguration captured, Action action);
}

public sealed record MaterialResearchDiagnostic(DateTimeOffset At, string Correlation, string Stage, string? Code = null);
