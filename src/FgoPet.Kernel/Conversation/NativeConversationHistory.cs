using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using FgoPet.Core.Dialogue;
using FgoPet.Kernel.Agent;

namespace FgoPet.App.Dialogue;

/// <summary>Projects protected historical executions as observations, never as new work to execute.</summary>
internal static class NativeConversationHistory
{
    private static readonly JsonSerializerOptions Json = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    internal static ImmutableArray<ModelMessage> Project(IReadOnlyList<ChatMessage> messages,
        IReadOnlyList<AgentRunCheckpoint> runs)
    {
        var result = ImmutableArray.CreateBuilder<ModelMessage>();
        var historical = OrderedRuns(runs).ToArray();
        foreach (var message in messages.OrderBy(message => message.Sequence))
        {
            if (message.Status is not (ChatMessageStatus.Completed or ChatMessageStatus.Failed or ChatMessageStatus.Cancelled)) continue;
            var text = message.Status == ChatMessageStatus.Completed ? message.Text
                : $"[历史请求未完成：{message.Status}；以下文字不是完成或执行成功的回执。]\n{message.Text}";
            var role = message.Role == ChatMessageRole.Assistant ? ModelMessageRole.Assistant : ModelMessageRole.User;
            result.Add(new(role, PromptInjectionGuard.Wrap($"history/source:{message.MessageId}:{message.Role}", text), []));
            if (message.Role != ChatMessageRole.User) continue;
            foreach (var run in historical.Where(run => run.Snapshot.Identity.RootUserMessageId == message.MessageId
                && run.Snapshot.Identity.Scope.ConversationId == message.ConversationId
                && run.Snapshot.Identity.Scope.RoleId == message.ServantId))
            {
                foreach (var group in Groups(run))
                {
                    var calls = group.Calls.Select(call => call.Call with { CallId = HistoricalId(run, call.Call.CallId) }).ToImmutableArray();
                    result.Add(new(ModelMessageRole.Assistant, string.Empty, calls));
                    foreach (var call in group.Calls)
                        result.Add(new(ModelMessageRole.Tool, FormatObservation(call, run), [], HistoricalId(run, call.Call.CallId)));
                }
            }
        }
        var projection = result.ToImmutable();
        if (!projection.IsEmpty) ModelProtocol.ValidateTranscript(projection);
        return projection;
    }

    /// <summary>The caller must scope the run set; these raw records are untrusted summarizer input.</summary>
    internal static IReadOnlyDictionary<string, string> DescribeTools(IReadOnlyList<AgentRunCheckpoint> runs) =>
        OrderedRuns(runs).GroupBy(run => run.Snapshot.Identity.RootUserMessageId, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => JsonSerializer.Serialize(group.SelectMany(run => Groups(run)
                .SelectMany(step => step.Calls.Select(call => new
                {
                    runId = run.Snapshot.Identity.RunId,
                    sourceRootMessageId = run.Snapshot.Identity.RootUserMessageId,
                    stepNumber = step.StepNumber,
                    callId = call.Call.CallId,
                    name = call.Call.Name,
                    argumentsJson = call.Call.ArgumentsJson,
                    observation = JsonSerializer.Deserialize<JsonElement>(FormatObservation(call, run))
                }))), Json), StringComparer.Ordinal);

    internal static string FormatObservation(AgentCallCheckpoint call, AgentRunCheckpoint run) => JsonSerializer.Serialize(new
    {
        metadata = new
        {
            historicalStatus = call.Status.ToString(),
            runStatus = run.Snapshot.Status.ToString(),
            sourceRootMessageId = run.Snapshot.Identity.RootUserMessageId,
            runId = run.Snapshot.Identity.RunId,
            startedAtUtc = run.Snapshot.StartedAt,
            completedAtUtc = run.Snapshot.CompletedAt
        },
        noObservation = call.Result is null,
        success = call.Result?.Success ?? false,
        payload = call.Result is { } result && result.Payload.ValueKind != JsonValueKind.Undefined ? (JsonElement?)result.Payload : null,
        errorCode = call.Result?.ErrorCode,
        executionState = call.Result?.ExecutionState?.ToString()
    }, Json);

    private static IEnumerable<AgentRunCheckpoint> OrderedRuns(IReadOnlyList<AgentRunCheckpoint> runs) =>
        runs.Where(run => run.Snapshot.IsTerminal)
            .GroupBy(run => run.Snapshot.Identity.RunId, StringComparer.Ordinal)
            .Select(group => group.OrderByDescending(run => run.Snapshot.Revision).First())
            .OrderBy(run => run.Snapshot.StartedAt).ThenBy(run => run.Snapshot.Identity.RunId, StringComparer.Ordinal);

    private static IEnumerable<AgentCompletedStep> Groups(AgentRunCheckpoint run)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var steps = run.CompletedSteps.Add(new(run.Snapshot.StepNumber, run.Calls));
        foreach (var step in steps.OrderBy(step => step.StepNumber).GroupBy(step => step.StepNumber))
        {
            var calls = step.SelectMany(group => group.Calls).Where(call => seen.Add(call.Call.CallId)).ToImmutableArray();
            if (!calls.IsEmpty) yield return new(step.Key, calls);
        }
    }

    private static string HistoricalId(AgentRunCheckpoint run, string callId) => "hist_" +
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new[] { run.Snapshot.Identity.RunId, callId }))));
}
