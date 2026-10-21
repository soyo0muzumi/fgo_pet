// 仅隔离验证器使用：诊断不参与接收、预算、重试或授权判定。
using System.IO;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using FgoPet.Core.Dialogue;
using FgoPet.Kernel.Agent;

namespace FgoPet.Testing.Persona;

internal sealed record P2ContentSummary(string Sha256, int Utf8Bytes, int Utf16Units, bool Complete);
internal sealed record P2JsonPosition(string Kind, long? ErrorLine = null, long? ErrorByteInLine = null,
    long? ErrorByteAbsolute = null, bool? ErrorAtEof = null);
internal sealed record P2ResponseDiagnostic(string? RequestHash, string ResponseIdState, P2ContentSummary Body,
    P2JsonPosition Json, string? Finish, bool HasToolCalls, string ParseState);
internal sealed record P2FinalCorrelation(string? RequestHash, int? RepairParentRequest, string? RepairParentHash,
    string LinkState, P2ContentSummary Body, P2JsonPosition Json, bool? WireBodyMatches);
[JsonConverter(typeof(JsonStringEnumConverter<P2ArgumentState>))]
internal enum P2ArgumentState { InvalidJson, NonObject, NonemptyObject, EmptyObject }
internal sealed record P2FinalizeValidation(int CallCount, bool ResolvedFinalize,
    bool BodyWhitespaceOnly, bool ArgumentsEmptyObject, P2ArgumentState[] ArgumentStates);

/** 每次请求独占观测器；只记录同一校验器拒绝的完整结束候选，不改变宿主判定。 */
internal sealed class P2FinalizeDiagnostics(ModelStepAggregator aggregator, IReadOnlySet<string> previousCallIds)
{
    private ModelStepAggregator? current = aggregator;
    internal P2FinalizeValidation? Rejection { get; private set; }

    internal static P2FinalizeDiagnostics? Create(ChatRequest request)
    {
        try
        {
            var control = ModelToolNameMap.GetWireName(NativeFinalization.Name);
            if (!request.IsAgentRequest || request.Tools is not { Count: > 0 } tools
                || !tools.Any(tool => tool.Name == control)) return null;
            // Request.Tools 已是 wire 名称；只把实际 offered 的结束工具还原为 canonical。
            var names = new ModelToolNameMap(tools.Select(tool => tool.Name == control ? NativeFinalization.Name : tool.Name));
            return new(new(names), request.ModelMessages.SelectMany(message => message.ToolCalls)
                .Select(call => call.CallId).ToHashSet(StringComparer.Ordinal));
        }
        catch { return null; } // 观测不可用不能替换原请求、聚合或取消异常。
    }

    internal void Add(ChatStreamChunk chunk)
    {
        if (current is not { } step) return;
        try
        {
            step.Add(chunk);
            if (!chunk.IsComplete) return;
            current = null; // 完成后不继续持有正文和参数缓冲。
            var response = step.Complete(previousCallIds);
            try { NativeFinalization.Validate(response); }
            catch (AgentProtocolException error) when (error.Code == "MODEL_INVALID_FINALIZE")
            {
                var controls = response.ToolCalls.Where(call => call.IsResolved && call.Name == NativeFinalization.Name).ToArray();
                var states = controls.Select(call => ClassifyArguments(call.ArgumentsJson)).ToArray();
                Rejection = new(response.ToolCalls.Length, controls.Length != 0,
                    string.IsNullOrWhiteSpace(response.AssistantMessage.Content), controls.Length != 0 && states.All(state =>
                        state == P2ArgumentState.EmptyObject), states);
            }
        }
        catch { current = null; } // 只放弃诊断；原 chunk、异常和迭代关闭仍由原链路处理。
    }

    private static P2ArgumentState ClassifyArguments(string raw)
    {
        try
        {
            using var document = JsonDocument.Parse(raw);
            return document.RootElement.ValueKind != JsonValueKind.Object ? P2ArgumentState.NonObject
                : document.RootElement.EnumerateObject().Any() ? P2ArgumentState.NonemptyObject : P2ArgumentState.EmptyObject;
        }
        catch (JsonException) { return P2ArgumentState.InvalidJson; }
    }
}

internal static class P2ResponseDiagnostics
{
    internal static string? SafeTurnHash(string? value) => value is { Length: 64 } && value.All(Uri.IsHexDigit) ? value : null;
    internal static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
    internal static P2ContentSummary Summarize(string text, bool complete = true) =>
        new(Hash(text), Encoding.UTF8.GetByteCount(text), text.Length, complete);

    internal static P2JsonPosition JsonPosition(string text)
    {
        try
        {
            using var document = JsonDocument.Parse(text);
            return new(document.RootElement.ValueKind.ToString().ToLowerInvariant());
        }
        catch (JsonException error)
        {
            var bytes = Encoding.UTF8.GetBytes(text);
            long? absolute = null;
            if (error.LineNumber is >= 0 && error.BytePositionInLine is >= 0)
            {
                var start = 0;
                for (var line = 0L; line < error.LineNumber; line++)
                {
                    var newline = Array.IndexOf(bytes, (byte)'\n', start);
                    if (newline < 0) { start = -1; break; }
                    start = newline + 1;
                }
                if (start >= 0 && start + error.BytePositionInLine <= bytes.Length)
                    absolute = start + error.BytePositionInLine;
            }
            return new("invalid", error.LineNumber, error.BytePositionInLine, absolute,
                absolute is null ? null : absolute == bytes.Length);
        }
        // 不保留异常原文、JsonException.Path 或输入片段。
        catch (InvalidOperationException) { return new("invalid-unicode"); }
    }

    internal static P2ResponseDiagnostic? ReadWire(byte[] bytes)
    {
        try
        {
            if (bytes.Length > 8 * 1024 * 1024) return null;
            var text = new UTF8Encoding(false, true).GetString(bytes);
            if (text.StartsWith('\uFEFF')) text = text[1..];
            using var reader = new StringReader(text);
            var content = new StringBuilder();
            string? idHash = null, finish = null;
            var missing = false;
            var invalid = false;
            var conflict = false;
            var tools = false;
            var done = false;
            var state = "parsed";
            string? line;
            while ((line = reader.ReadLine()) is not null)
            {
                if (!line.StartsWith("data:", StringComparison.OrdinalIgnoreCase)) continue;
                var data = line[5..].Trim();
                if (done) { state = "after-done"; break; }
                if (data == "[DONE]") { done = true; continue; }
                try
                {
                    using var frame = JsonDocument.Parse(data);
                    var root = frame.RootElement;
                    if (!root.TryGetProperty("id", out var id)) missing = true;
                    else if (id.ValueKind != JsonValueKind.String || id.GetString() is not { Length: > 0 and <= 256 } value
                        || value.Any(character => character is < '!' or > '~')) invalid = true;
                    else
                    {
                        var hash = Hash(value);
                        if (idHash is not null && idHash != hash) conflict = true;
                        idHash ??= hash;
                    }
                    if (!root.TryGetProperty("choices", out var choices) || choices.ValueKind != JsonValueKind.Array) continue;
                    foreach (var choice in choices.EnumerateArray())
                    {
                        if (choice.TryGetProperty("delta", out var delta) && delta.ValueKind == JsonValueKind.Object)
                        {
                            if (delta.TryGetProperty("content", out var body) && body.ValueKind == JsonValueKind.String) content.Append(body.GetString());
                            tools |= delta.TryGetProperty("tool_calls", out var calls) && calls.ValueKind == JsonValueKind.Array && calls.GetArrayLength() != 0;
                        }
                        if (choice.TryGetProperty("finish_reason", out var reason) && reason.ValueKind != JsonValueKind.Null)
                            finish = reason.ValueKind == JsonValueKind.String && reason.GetString() is "stop" or "length" or "tool_calls"
                                ? reason.GetString() : "other";
                    }
                }
                catch (Exception error) when (error is JsonException or InvalidOperationException)
                { state = "frame-invalid"; break; } // 此时只给前缀摘要，不跳过损坏帧后假称完整。
            }
            var bodyText = content.ToString();
            var idState = conflict ? "conflicting" : invalid ? "invalid" : idHash is null ? "missing" : missing ? "partial" : "consistent";
            return new(conflict || invalid ? null : idHash, idState, Summarize(bodyText, done && finish is not null && state == "parsed"),
                JsonPosition(bodyText), finish, tools, state);
        }
        catch { return null; } // 诊断失败只留下缺口，不覆盖原 usage/parser 异常。
    }
}

/** 按测试进程生成的每轮 scope 关联；只接受显式 repair 事件，不检查提示词来猜父请求。 */
internal sealed class P2RepairLinker
{
    private sealed class Entry(string scope, int? parent)
    {
        internal string Scope { get; } = scope;
        internal int? Parent { get; } = parent;
        internal P2ResponseDiagnostic? Response { get; set; }
        internal bool FinalObserved { get; set; }
    }
    private readonly object gate = new();
    private readonly Dictionary<int, Entry> entries = [];
    private readonly Dictionary<string, int> pending = [];
    private static bool ValidScope(string scope) => scope.Length == 64 && scope.All(Uri.IsHexDigit);

    internal void BeginRequest(string scope, int request)
    {
        lock (gate)
        {
            if (!ValidScope(scope) || request < 1 || entries.Count >= 64) return;
            var hasParent = pending.Remove(scope, out var parent);
            entries[request] = new(scope, hasParent ? parent : null);
        }
    }
    internal void Observe(P2RequestDiagnostic item)
    {
        lock (gate)
            if (item.Response is not null && entries.TryGetValue(item.Attempt, out var entry)) entry.Response = item.Response;
    }
    internal void MarkRepair(string scope, int parent)
    {
        lock (gate)
            if (entries.TryGetValue(parent, out var entry) && entry.Scope == scope && entry.FinalObserved && pending.Count < 64)
                pending[scope] = parent;
    }
    internal P2FinalCorrelation? CaptureFinal(string scope, int request, string raw)
    {
        try
        {
            lock (gate)
            {
                if (!entries.TryGetValue(request, out var entry) || entry.Scope != scope) return null;
                entry.FinalObserved = true;
                var body = P2ResponseDiagnostics.Summarize(raw);
                var wire = entry.Response;
                var parent = entry.Parent is { } parentId && entries.TryGetValue(parentId, out var prior) && prior.Scope == scope ? prior : null;
                return new(wire?.RequestHash, parent is null ? null : entry.Parent, parent?.Response?.RequestHash,
                    wire?.RequestHash is null ? "response-id-unavailable" : parent is not null && parent.Response?.RequestHash is null ? "parent-id-unavailable" : "linked",
                    body, P2ResponseDiagnostics.JsonPosition(raw), wire?.Body.Complete == true ? body.Sha256 == wire.Body.Sha256 && body.Utf8Bytes == wire.Body.Utf8Bytes : null);
            }
        }
        catch { return null; }
    }
}

/** 仅显式运行的合成 contract 场景使用；正文与模板不得写入脱敏运行日志。 */
internal sealed class P2FailedCandidateCapture : IDisposable
{
    internal const string FileName = "failed-candidates.private.jsonl";
    internal const string MetadataFileName = "failed-candidates.metadata.jsonl";
    internal const string RepairInstructions =
        "The final answer failed JSON validation. Correct its format once: return a complete JSON object with text and emotion. " +
        "Escape quotes and control characters. Preserve the answer, source attribution and uncertainty; do not invent facts or actions. " +
        "No tools are available; do not request or repeat any tool call.";
    private sealed record Template(int MessageIndex, string Kind, string Role, string Text, P2ContentSummary Body);
    private sealed record RequestTemplate(string Scope, Template[] Templates);
    private readonly object gate = new();
    private readonly Dictionary<int, RequestTemplate> requests = [];
    private readonly HashSet<int> captured = [];
    private readonly FileStream file;
    private readonly bool metadataOnly;
    private bool failed;

    internal P2FailedCandidateCapture(string report, bool metadataOnly = false)
    {
        this.metadataOnly = metadataOnly;
        // 在创建文件时一次性设置 ACL，不能先用继承权限写入正文再收紧。
        var security = new FileSecurity();
        security.SetAccessRuleProtection(true, false);
        using var identity = WindowsIdentity.GetCurrent();
        foreach (var sid in new[] { identity.User!, new SecurityIdentifier("S-1-5-18"), new SecurityIdentifier("S-1-5-32-544") })
            security.AddAccessRule(new FileSystemAccessRule(sid, FileSystemRights.FullControl, AccessControlType.Allow));
        file = new FileInfo(Path.Combine(report, metadataOnly ? MetadataFileName : FileName)).Create(FileMode.CreateNew, FileSystemRights.Write,
            FileShare.Read, 4096, FileOptions.WriteThrough, security);
    }

    internal bool ObserveRequest(string scope, int request, ChatRequest input, string finalTemplate)
    {
        lock (gate)
        {
            if (failed || scope.Length != 64 || !scope.All(Uri.IsHexDigit) || request < 1 || requests.Count >= 64 || requests.ContainsKey(request))
                return false;
            // 保存实际 request 中的固定格式消息；不保存 persona、历史、用户输入或工具参数。
            var templates = input.ModelMessages.Select((message, index) => (message, index))
                .Where(x => x.message.Role == ModelMessageRole.System &&
                    (x.message.Content == finalTemplate || x.message.Content == RepairInstructions))
                .Select(x => new Template(x.index, x.message.Content == finalTemplate ? "final-contract" : "format-repair",
                    "System", x.message.Content, P2ResponseDiagnostics.Summarize(x.message.Content))).ToArray();
            var toolStep = input.Tools is { Count: > 0 } && input.ModelMessages.Any(message =>
                message.Role == ModelMessageRole.System && message.Content == NativeFinalization.Instructions);
            if (templates.Count(x => x.Kind == "final-contract") != (toolStep ? 0 : 1)) return false;
            requests.Add(request, new(scope, templates));
            return true;
        }
    }

    internal bool TryCapture(string scene, string scope, int request, string raw, string strictValidation, P2FinalCorrelation? correlation)
    {
        lock (gate)
        {
            if (failed || !requests.TryGetValue(request, out var input) || input.Scope != scope || captured.Contains(request)) return false;
            try
            {
                if (raw.Length > 8 * 1024 * 1024) { failed = true; return false; }
                object record;
                if (metadataOnly)
                {
                    record = new
                    {
                        Schema = 2, CaptureMode = "metadata-only", At = DateTimeOffset.UtcNow, Scene = scene, ScopeHash = scope, Request = request,
                        correlation?.RequestHash, correlation?.RepairParentRequest, correlation?.RepairParentHash,
                        StrictValidation = strictValidation, Body = P2ResponseDiagnostics.Summarize(raw),
                        Templates = input.Templates.Select(template => new { template.MessageIndex, template.Kind, template.Role, template.Body }).ToArray()
                    };
                }
                else
                {
                    // UTF-16 代码单元另存，避免孤立代理项经过 JSON/UTF-8 替换后失去原文。
                    var units = new byte[raw.Length * 2];
                    for (var i = 0; i < raw.Length; i++)
                    {
                        units[i * 2] = (byte)raw[i];
                        units[i * 2 + 1] = (byte)(raw[i] >> 8);
                    }
                    var wellFormed = true;
                    try { _ = new UTF8Encoding(false, true).GetByteCount(raw); }
                    catch (EncoderFallbackException) { wellFormed = false; }
                    record = new
                    {
                        Schema = 1, At = DateTimeOffset.UtcNow, Scene = scene, ScopeHash = scope, Request = request,
                        correlation?.RequestHash, correlation?.RepairParentRequest, correlation?.RepairParentHash,
                        StrictValidation = strictValidation, Body = P2ResponseDiagnostics.Summarize(raw),
                        RawText = wellFormed ? raw : null, RawUtf16LeBase64 = Convert.ToBase64String(units),
                        RawUtf16Sha256 = Convert.ToHexString(SHA256.HashData(units)).ToLowerInvariant(), input.Templates
                    };
                }
                file.Write(JsonSerializer.SerializeToUtf8Bytes(record));
                file.WriteByte((byte)'\n');
                file.Flush(flushToDisk: true);
                captured.Add(request);
                return true;
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or ObjectDisposedException or JsonException)
            { failed = true; return false; } // 不记录异常消息或未成功留存的正文。
        }
    }

    public void Dispose() => file.Dispose();
}
