using System.Text;
using System.Text.Json;
using System.Collections.Immutable;
using FgoPet.Kernel.Agent;
using FgoPet.Dialogue.Contracts;
using FgoPet.UiSdk;

namespace FgoPet.App.Dialogue;

internal sealed record ChatWebCommand(
    string Type,
    ChatWebIdentity? Identity = null,
    string? Text = null,
    long? Revision = null,
    bool? OnlyCurrentProject = null,
    bool? Append = null,
    string? TargetConversationId = null,
    string? Action = null,
    string? TargetId = null,
    bool? Expanded = null,
    string? RunId = null,
    string? InputRequestId = null,
    ImmutableArray<QuestionAnswer> Answers = default);

/// <summary>Strictly reads the closed JSON schemas used by the Chat Web bridge.</summary>
internal static class ChatWebCommandReader
{
    public const int MaxEnvelopeBytes = 65_536;
    public const int MaxDraftChars = 12_000;

    public static bool TryRead(WebSurfaceMessage message, out ChatWebCommand? command, out string errorCode)
    {
        try { return TryReadCore(message, out command, out errorCode); }
        catch (Exception error) when (error is JsonException or InvalidOperationException or ObjectDisposedException)
        {
            command = null;
            errorCode = "CHAT_COMMAND_INVALID";
            return false;
        }
    }

    private static bool TryReadCore(WebSurfaceMessage message, out ChatWebCommand? command, out string errorCode)
    {
        command = null;
        errorCode = "CHAT_COMMAND_INVALID";
        if (message is null || string.IsNullOrEmpty(message.RequestId)
            || message.Payload.ValueKind != JsonValueKind.Object
            || Encoding.UTF8.GetByteCount(message.Payload.GetRawText()) > MaxEnvelopeBytes)
        {
            return false;
        }

        var fields = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var property in message.Payload.EnumerateObject())
        {
            if (!fields.TryAdd(property.Name, property.Value)) return false;
        }

        if (message.Type == "chat.get")
        {
            if (fields.Count != 0) return false;
            command = new(message.Type);
            errorCode = string.Empty;
            return true;
        }

        if (message.Type == "chat.draft"
            && fields.TryGetValue("text", out var draftText)
            && draftText.ValueKind == JsonValueKind.String
            && draftText.GetString() is { Length: > MaxDraftChars })
        {
            errorCode = "CHAT_DRAFT_TOO_LONG";
            return false;
        }

        if (!TryReadIdentity(fields, out var identity)) return false;
        string? text = null;
        long? revision = null;
        bool? onlyCurrentProject = null;
        bool? append = null;
        string? targetConversationId = null;
        string? action = null;
        string? targetId = null;
        bool? expanded = null;
        string? runId = null;
        string? inputRequestId = null;
        ImmutableArray<QuestionAnswer> answers = default;

        switch (message.Type)
        {
            case "chat.draft":
                if (!Only(fields, "sessionId", "servantId", "conversationId", "text", "revision")
                    || !ReadString(fields, "text", allowEmpty: true, MaxDraftChars, out text)
                    || !ReadPositiveInt64(fields, "revision", out var draftRevision)) return false;
                revision = draftRevision;
                break;
            case "chat.send":
            case "chat.stop":
            case "chat.new":
            case "chat.history.cancelDelete":
            case "chat.retryCapability":
                if (fields.Count != 3) return false;
                break;
            case "chat.input.answer":
            case "chat.input.cancel":
                var inputFields = new List<string> { "sessionId", "servantId", "conversationId", "runId", "requestId", "revision" };
                if (message.Type == "chat.input.answer") inputFields.Add("answers");
                if (!Only(fields, inputFields.ToArray())
                    || !ReadString(fields, "runId", false, 128, out runId)
                    || !ReadString(fields, "requestId", false, 128, out inputRequestId)
                    || !ReadPositiveInt64(fields, "revision", out var inputRevision)) return false;
                revision = inputRevision;
                if (message.Type == "chat.input.answer" && !ReadAnswers(fields["answers"], out answers)) return false;
                break;
            case "chat.history":
                if (!Only(fields, "sessionId", "servantId", "conversationId", "onlyCurrentProject", "append")
                    || !ReadBoolean(fields, "onlyCurrentProject", out var filter)
                    || !ReadBoolean(fields, "append", out var isAppend)) return false;
                onlyCurrentProject = filter;
                append = isAppend;
                break;
            case "chat.history.open":
            case "chat.history.requestDelete":
            case "chat.history.confirmDelete":
                if (!Only(fields, "sessionId", "servantId", "conversationId", "targetConversationId")
                    || !ReadString(fields, "targetConversationId", allowEmpty: false, 128, out targetConversationId)) return false;
                break;
            case "chat.host":
                if (!fields.TryGetValue("action", out var actionElement)
                    || actionElement.ValueKind != JsonValueKind.String
                    || actionElement.GetString() is not { } actionName
                    || !IsKnownHostAction(actionName)) return false;
                action = actionName;
                var targetRequired = actionName is "copyTurn" or "readTurn" or "openSource" or "openWorkspace" or "selectProject" or "selectModel" or "removeContextChip";
                var expandedRequired = actionName == "setExpanded";
                var expected = new List<string> { "sessionId", "servantId", "conversationId", "action" };
                if (targetRequired) expected.Add("targetId");
                if (expandedRequired) expected.Add("expanded");
                if (!Only(fields, expected.ToArray())) return false;
                if (targetRequired && !ReadString(fields, "targetId", allowEmpty: false, 128, out targetId)) return false;
                var expandedValue = false;
                if (expandedRequired && !ReadBoolean(fields, "expanded", out expandedValue)) return false;
                if (expandedRequired) expanded = expandedValue;
                break;
            default:
                return false;
        }

        command = new(message.Type, identity, text, revision, onlyCurrentProject, append,
            targetConversationId, action, targetId, expanded, runId, inputRequestId, answers);
        errorCode = string.Empty;
        return true;
    }

    private static bool TryReadIdentity(IReadOnlyDictionary<string, JsonElement> fields, out ChatWebIdentity identity)
    {
        identity = null!;
        if (!ReadString(fields, "sessionId", allowEmpty: false, 64, out var sessionId)
            || !ReadString(fields, "servantId", allowEmpty: true, 128, out var servantId)
            || !ReadString(fields, "conversationId", allowEmpty: true, 128, out var conversationId)) return false;
        identity = new(sessionId!, servantId!, conversationId!);
        return true;
    }

    private static bool ReadAnswers(JsonElement value, out ImmutableArray<QuestionAnswer> answers)
    {
        answers = default;
        if (value.ValueKind != JsonValueKind.Array || value.GetArrayLength() is < 1 or > 3) return false;
        var result = ImmutableArray.CreateBuilder<QuestionAnswer>(value.GetArrayLength());
        foreach (var answer in value.EnumerateArray())
        {
            if (answer.ValueKind != JsonValueKind.Object) return false;
            var fields = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
            foreach (var property in answer.EnumerateObject()) if (!fields.TryAdd(property.Name, property.Value)) return false;
            if (!Only(fields, "questionId", "selectedOptionIndices", "text")
                || !ReadString(fields, "questionId", false, 64, out var id)
                || fields["selectedOptionIndices"].ValueKind != JsonValueKind.Array
                || fields["selectedOptionIndices"].GetArrayLength() > 8) return false;
            var indices = ImmutableArray.CreateBuilder<int>(fields["selectedOptionIndices"].GetArrayLength());
            foreach (var index in fields["selectedOptionIndices"].EnumerateArray())
            {
                if (index.ValueKind != JsonValueKind.Number || !index.TryGetInt32(out var number)) return false;
                indices.Add(number);
            }
            var text = fields["text"];
            if (text.ValueKind is not (JsonValueKind.Null or JsonValueKind.String)
                || text.ValueKind == JsonValueKind.String && text.GetString()!.Length > MaxDraftChars) return false;
            result.Add(new(id!, indices.MoveToImmutable(), text.ValueKind == JsonValueKind.Null ? null : text.GetString()));
        }
        answers = result.MoveToImmutable();
        return true;
    }

    private static bool Only(IReadOnlyDictionary<string, JsonElement> fields, params string[] allowed)
    {
        var set = new HashSet<string>(allowed, StringComparer.Ordinal);
        return fields.Count == set.Count && fields.Keys.All(set.Contains);
    }

    private static bool ReadString(IReadOnlyDictionary<string, JsonElement> fields, string name,
        bool allowEmpty, int maxLength, out string? value)
    {
        value = null;
        if (!fields.TryGetValue(name, out var element) || element.ValueKind != JsonValueKind.String) return false;
        value = element.GetString();
        return value is not null && (allowEmpty || value.Length > 0) && value.Length <= maxLength;
    }

    private static bool ReadBoolean(IReadOnlyDictionary<string, JsonElement> fields, string name, out bool value)
    {
        value = false;
        if (!fields.TryGetValue(name, out var element) || element.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) return false;
        value = element.GetBoolean();
        return true;
    }

    private static bool ReadPositiveInt64(IReadOnlyDictionary<string, JsonElement> fields, string name, out long value)
    {
        value = 0;
        return fields.TryGetValue(name, out var element)
            && element.ValueKind == JsonValueKind.Number
            && element.TryGetInt64(out value)
            && value > 0;
    }

    private static bool IsKnownHostAction(string action) => action is
        "hide" or "setExpanded" or "copyTurn" or "readTurn" or "openSource" or "openWorkspace"
        or "openFocus" or "openWorkspaceOverview" or "newWorkspaceItem"
        or "openPersonalizationSettings" or "openSpeechSettings" or "openModelSettings"
        or "refreshProjects" or "selectProject" or "selectModel" or "removeContextChip"
        or "chooseWorkspace" or "clearWorkspace";
}
