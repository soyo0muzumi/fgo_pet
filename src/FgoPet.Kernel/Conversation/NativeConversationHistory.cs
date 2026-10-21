using System.Collections.Immutable;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using FgoPet.Core.Dialogue;
using FgoPet.Kernel.Agent;

namespace FgoPet.App.Dialogue;

/// <summary>Attaches historical execution evidence to its source turn, never as current tool protocol.</summary>
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
            var content = PromptInjectionGuard.Wrap($"history/source:{message.MessageId}:{message.Role}", text);
            if (message.Role == ChatMessageRole.User)
            {
                var evidence = SerializeTools(historical.Where(run => run.Snapshot.Identity.RootUserMessageId == message.MessageId
                    && run.Snapshot.Identity.Scope.ConversationId == message.ConversationId
                    && run.Snapshot.Identity.Scope.RoleId == message.ServantId));
                if (evidence != "[]")
                {
                    var source = $"historical_execution:{message.MessageId}";
                    var combined = content + "\n" + PromptInjectionGuard.Wrap(source + ":0", evidence);
                    if (Encoding.UTF8.GetByteCount(combined) <= ModelProtocol.MaxContentBytes) content = combined;
                    else
                    {
                        // Keep the original turn, then contiguous data fragments with source/offset.
                        // As in summary input, split by position only. Even worst-case XML escaping
                        // of 16K UTF-16 units stays below the unchanged 128 KiB message limit.
                        result.Add(new(role, content, []));
                        for (var offset = 0; offset < evidence.Length;)
                        {
                            var length = Math.Min(16_000, evidence.Length - offset);
                            if (offset + length < evidence.Length && char.IsHighSurrogate(evidence[offset + length - 1])) length--;
                            result.Add(new(role, PromptInjectionGuard.Wrap($"{source}:{offset}", evidence.Substring(offset, length)), []));
                            offset += length;
                        }
                        continue;
                    }
                }
            }
            result.Add(new(role, content, []));
        }
        var projection = result.ToImmutable();
        if (!projection.IsEmpty) ModelProtocol.ValidateTranscript(projection);
        return projection;
    }

    /// <summary>The caller must scope the run set; these raw records are untrusted summarizer input.</summary>
    internal static IReadOnlyDictionary<string, string> DescribeTools(IReadOnlyList<AgentRunCheckpoint> runs) =>
        OrderedRuns(runs).GroupBy(run => run.Snapshot.Identity.RootUserMessageId, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => SerializeTools(group), StringComparer.Ordinal);

    private static string SerializeTools(IEnumerable<AgentRunCheckpoint> runs) =>
        JsonSerializer.Serialize(runs.SelectMany(run => Groups(run)
                .SelectMany(step => step.Calls.Where(call => call.Call.Name != NativeFinalization.Name).Select(call => new
                {
                    sourceKind = "historical_execution",
                    runId = run.Snapshot.Identity.RunId,
                    sourceRootMessageId = run.Snapshot.Identity.RootUserMessageId,
                    stepNumber = step.StepNumber,
                    callId = call.Call.CallId,
                    name = call.Call.Name,
                    argumentsJson = call.Call.ArgumentsJson,
                    observation = JsonSerializer.Deserialize<JsonElement>(FormatObservation(call, run))
                }))), Json);

    internal static string FormatObservation(AgentCallCheckpoint call, AgentRunCheckpoint run) => JsonSerializer.Serialize(new
    {
        metadata = new
        {
            approvalEvidence = "NotIncludedInThisProjection",
            historicalStatus = call.Status.ToString(),
            toolEffect = call.Effect?.ToString(),
            runStatus = run.Snapshot.Status.ToString(),
            sourceRootMessageId = run.Snapshot.Identity.RootUserMessageId,
            runId = run.Snapshot.Identity.RunId,
            startedAtUtc = run.Snapshot.StartedAt.ToUniversalTime(),
            completedAtUtc = run.Snapshot.CompletedAt?.ToUniversalTime()
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

}
