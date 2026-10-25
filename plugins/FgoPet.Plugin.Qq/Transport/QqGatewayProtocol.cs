using System.Globalization;
using System.Text.Json;

namespace FgoPet.Plugin.Qq.Transport;

// Protocol sources (reviewed against official SDK commit 429a0f1b798f3b0fcef3679e207dbc0469a1df2d):
// https://bot.q.qq.com/wiki/develop/api-v2/dev-prepare/interface-framework/event-emit.html
// https://bot.q.qq.com/wiki/develop/api-v2/autogen/event/c2c_message_create.html
// https://github.com/tencent-connect/qqbot-nodejs/tree/429a0f1b798f3b0fcef3679e207dbc0469a1df2d/src/protocol/gateway
internal static class QqGatewayProtocol
{
    internal const int MaxFrameBytes = 64 * 1024;
    internal const int MaxTextLength = 12_000;
    internal const int C2cIntent = 1 << 25;
    internal const int TextChunkLength = 5_000;
    // The current message overview / endpoint introduction says 60 minutes, but that endpoint's msg_id
    // field still says 5 minutes. This immediate-reply transport deliberately uses the shorter bound.
    // https://bot.q.qq.com/wiki/develop/api-v2/autogen/api/v2_users_user_openid_messages.post.html
    internal static readonly TimeSpan ReplyWindow = TimeSpan.FromMinutes(5);

    internal static bool IsConnectionValid(QqBotConnection? connection) => connection is not null
        && connection.AppId is { Length: > 0 and <= 64 } && connection.AppId.All(char.IsAsciiDigit)
        && connection.AppSecretCredentialTarget is { Length: > 0 and <= 256 }
        && !string.IsNullOrWhiteSpace(connection.AppSecretCredentialTarget)
        && !connection.AppSecretCredentialTarget.Any(char.IsControl);

    internal static bool IsGatewayAllowed(Uri? uri) => uri is { IsAbsoluteUri: true }
        && uri.Scheme == "wss" && uri.Port == 443 && uri.UserInfo.Length == 0 && uri.Fragment.Length == 0
        && (uri.IdnHost.Equals("api.bot.qq.com", StringComparison.OrdinalIgnoreCase)
            || uri.IdnHost.Equals("api.sgroup.qq.com", StringComparison.OrdinalIgnoreCase));

    internal static QqGatewayFrame Parse(ReadOnlyMemory<byte> bytes)
    {
        if (bytes.Length > MaxFrameBytes) throw new QqTransportException("QQ_FRAME_TOO_LARGE");
        try
        {
            using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 24 });
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("op", out var operation)
                || !operation.TryGetInt32(out var op)) throw new QqTransportException("QQ_FRAME_INVALID");
            long? sequence = null;
            if (root.TryGetProperty("s", out var seq) && seq.ValueKind != JsonValueKind.Null)
            {
                if (!seq.TryGetInt64(out var number) || number < 0) throw new QqTransportException("QQ_FRAME_INVALID");
                sequence = number;
            }
            string? eventType = null;
            if (root.TryGetProperty("t", out var type) && type.ValueKind != JsonValueKind.Null)
            {
                if (type.ValueKind != JsonValueKind.String || type.GetString() is not { Length: <= 128 } value)
                    throw new QqTransportException("QQ_FRAME_INVALID");
                eventType = value;
            }
            var data = root.TryGetProperty("d", out var payload) ? payload.Clone() : default;
            return new(op, sequence, eventType, data);
        }
        catch (JsonException) { throw new QqTransportException("QQ_FRAME_INVALID"); }
        catch (InvalidOperationException) { throw new QqTransportException("QQ_FRAME_INVALID"); }
    }

    internal static QqC2cMessage? ReadTextMessage(QqGatewayFrame frame)
    {
        if (frame.Op != 0 || frame.EventType != "C2C_MESSAGE_CREATE" || frame.Data.ValueKind != JsonValueKind.Object)
            return null;
        var data = frame.Data;
        if (data.TryGetProperty("message_type", out var type)
            && (type.ValueKind != JsonValueKind.Number || !type.TryGetInt32(out var kind) || kind != 0)) return null;
        if (HasContent(data, "attachments") || HasContent(data, "ark_data") || HasNonTextElements(data)) return null;
        if (!TryString(data, "id", 512, out var id) || string.IsNullOrWhiteSpace(id) || id.Any(char.IsControl)
            || !TryString(data, "content", MaxTextLength, out var content)
            || string.IsNullOrWhiteSpace(content) || content.Contains('\0') || !IsValidUtf16(content)
            || !data.TryGetProperty("author", out var author) || author.ValueKind != JsonValueKind.Object
            || !TryString(author, "user_openid", 128, out var user) || !IsOpenId(user)
            || (author.TryGetProperty("bot", out var bot) && bot.ValueKind != JsonValueKind.False && bot.ValueKind != JsonValueKind.Null)
            || !TryString(data, "timestamp", 64, out var timestamp)
            || !DateTimeOffset.TryParse(timestamp, CultureInfo.InvariantCulture, DateTimeStyles.None, out var time)) return null;
        return new(id, user, content, time);
    }

    internal static QqC2cMessage? ReadUnsupportedMediaMessage(QqGatewayFrame frame)
    {
        if (frame.Op != 0 || frame.EventType != "C2C_MESSAGE_CREATE" || frame.Data.ValueKind != JsonValueKind.Object)
            return null;
        var data = frame.Data;
        if (!data.TryGetProperty("attachments", out var attachments) || attachments.ValueKind != JsonValueKind.Array
            || attachments.GetArrayLength() is < 1 or > 16) return null;
        if (data.TryGetProperty("message_type", out var type)
            && (type.ValueKind != JsonValueKind.Number || !type.TryGetInt32(out var kind) || kind < 0)) return null;
        if (!TryString(data, "id", 512, out var id) || string.IsNullOrWhiteSpace(id) || id.Any(char.IsControl)
            || !data.TryGetProperty("author", out var author) || author.ValueKind != JsonValueKind.Object
            || !TryString(author, "user_openid", 128, out var user) || !IsOpenId(user)
            || (author.TryGetProperty("bot", out var bot) && bot.ValueKind != JsonValueKind.False && bot.ValueKind != JsonValueKind.Null)
            || !TryString(data, "timestamp", 64, out var timestamp)
            || !DateTimeOffset.TryParse(timestamp, CultureInfo.InvariantCulture, DateTimeStyles.None, out var time)) return null;
        // Do not extract captions, attachment URLs, names or bytes; this envelope only permits a text-only notice.
        return new(id, user, string.Empty, time, IsUnsupportedMedia: true);
    }

    internal static bool TryString(JsonElement data, string name, int limit, out string value)
    {
        value = string.Empty;
        if (data.ValueKind != JsonValueKind.Object || !data.TryGetProperty(name, out var property)
            || property.ValueKind != JsonValueKind.String || property.GetString() is not { } text
            || text.Length is 0 || text.Length > limit) return false;
        value = text;
        return true;
    }

    internal static bool IsOpenId(string? value) => value is { Length: > 0 and <= 128 }
        && value.All(character => char.IsAsciiLetterOrDigit(character) || character is '_' or '-');

    internal static bool IsValidUtf16(string value)
    {
        for (var index = 0; index < value.Length; index++)
        {
            if (char.IsHighSurrogate(value[index]))
            {
                if (++index >= value.Length || !char.IsLowSurrogate(value[index])) return false;
            }
            else if (char.IsLowSurrogate(value[index])) return false;
        }
        return true;
    }

    private static bool HasContent(JsonElement data, string name) => data.TryGetProperty(name, out var value)
        && value.ValueKind is not JsonValueKind.Null and not JsonValueKind.Undefined
        && !(value.ValueKind == JsonValueKind.Array && value.GetArrayLength() == 0);

    private static bool HasNonTextElements(JsonElement data)
    {
        if (!data.TryGetProperty("msg_elements", out var elements) || elements.ValueKind == JsonValueKind.Null) return false;
        if (elements.ValueKind != JsonValueKind.Array || elements.GetArrayLength() > 128) return true;
        foreach (var element in elements.EnumerateArray())
        {
            if (element.ValueKind != JsonValueKind.Object || HasContent(element, "attachments")
                || HasContent(element, "ark_data") || HasContent(element, "msg_elements")) return true;
            if (element.TryGetProperty("message_type", out var type)
                && (type.ValueKind != JsonValueKind.Number || !type.TryGetInt32(out var kind) || kind != 0)) return true;
            if (element.TryGetProperty("content", out var content) && content.ValueKind != JsonValueKind.String) return true;
        }
        // Plain text elements are optional metadata. Only the top-level sender's content enters the host.
        return false;
    }
}

internal sealed record QqGatewayFrame(int Op, long? Sequence, string? EventType, JsonElement Data);

/// <summary>Never wraps a raw network exception or platform response body.</summary>
internal sealed class QqTransportException(string code, bool terminal = false, bool clearSession = false,
    bool refreshToken = false, TimeSpan? retryDelay = null) : Exception(code)
{
    internal string Code { get; } = code;
    internal bool Terminal { get; } = terminal;
    internal bool ClearSession { get; } = clearSession;
    internal bool RefreshToken { get; } = refreshToken;
    internal TimeSpan? RetryDelay { get; } = retryDelay;
}
