using System.Buffers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FgoPet.Extensibility;

namespace FgoPet.Kernel.Agent;

/// <summary>Pure frozen-request validation. An Allow answer is not an execution permit.</summary>
public sealed class ApprovalBroker
{
    private readonly TimeProvider _time;
    private readonly TimeSpan _ttl;
    public ApprovalBroker(TimeProvider? time = null, TimeSpan? ttl = null)
        => (_time, _ttl) = (time ?? TimeProvider.System, InteractionValidation.Ttl(ttl));

    public ApprovalRequest Create(AgentRunIdentity identity, int stepNumber, string callId,
        long waitingRevision, RegisteredTool tool, string pluginVersion, JsonElement arguments,
        string? rootAuthorizationId = null, ToolResourceAuthorization? resource = null,
        ToolBusinessConfirmation? businessConfirmation = null)
    {
        var binding = InteractionValidation.CreateBinding(identity, stepNumber, callId, waitingRevision, _time, _ttl);
        if (tool is null || tool.Descriptor is null || !InteractionValidation.BoundedId(tool.PluginId, 128)
            || !InteractionValidation.BoundedId(pluginVersion, 64)
            || !char.IsAsciiLetterOrDigit(pluginVersion[0])
            || !pluginVersion.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-' or '+')
            || rootAuthorizationId is not null && !InteractionValidation.BoundedId(rootAuthorizationId, 128)) throw InvalidApproval();
        if (resource is not null && (!InteractionValidation.BoundedId(resource.Id, 128) || resource.Revision < 0 ||
            resource.Fingerprint is not null && (resource.Fingerprint.Length != 64 ||
                !resource.Fingerprint.All(c => c is >= 'A' and <= 'F' or >= '0' and <= '9')))) throw InvalidApproval();
        if (businessConfirmation is not null && (!InteractionValidation.BoundedId(businessConfirmation.DraftId, 128) ||
            businessConfirmation.Version < 1 || businessConfirmation.Fingerprint is not { Length: 64 } ||
            !businessConfirmation.Fingerprint.All(c => c is >= 'A' and <= 'F' or >= '0' and <= '9'))) throw InvalidApproval();
        var normalized = CanonicalObject(arguments);
        var schema = CanonicalObject(tool.Descriptor.Parameters);
        if (!ToolArgumentsValidator.Validate(tool.Descriptor.Parameters, arguments, out _)) throw InvalidApproval();
        return new(binding, new(tool.PluginId, pluginVersion, tool.Descriptor.Name, Digest(normalized), Digest(schema),
            rootAuthorizationId, identity.AuthorizationRevision) { Resource = resource, BusinessConfirmation = businessConfirmation }, normalized);
    }

    public ApprovalDecision Validate(ApprovalRequest request, ApprovalReply reply, ToolScope scope)
    {
        if (request is null || reply is null) throw new AgentStateException("RUN_INTERACTION_STALE");
        InteractionValidation.Match(request.Binding, reply.RunId, reply.RequestId, reply.ExpectedRevision, scope, _time);
        if (!Enum.IsDefined(reply.Decision)) throw new AgentStateException("RUN_INVALID_APPROVAL_REPLY");
        return reply.Decision;
    }

    private static string CanonicalObject(JsonElement value)
    {
        try
        {
            if (value.ValueKind != JsonValueKind.Object) throw InvalidApproval();
            var output = new ArrayBufferWriter<byte>();
            using (var writer = new Utf8JsonWriter(output))
            {
                WriteCanonical(writer, value, 1);
                writer.Flush();
            }
            if (output.WrittenCount > ModelProtocol.MaxArgumentsBytes) throw InvalidApproval();
            return Encoding.UTF8.GetString(output.WrittenSpan);
        }
        catch (Exception error) when (error is JsonException or InvalidOperationException or ObjectDisposedException or ArgumentException)
        { throw InvalidApproval(); }
    }

    private static void WriteCanonical(Utf8JsonWriter writer, JsonElement value, int depth)
    {
        if (depth > 32 && value.ValueKind is JsonValueKind.Object or JsonValueKind.Array) throw InvalidApproval();
        switch (value.ValueKind)
        {
            case JsonValueKind.Object:
                var properties = value.EnumerateObject().ToArray();
                if (properties.Select(property => property.Name).Distinct(StringComparer.Ordinal).Count() != properties.Length)
                    throw InvalidApproval();
                writer.WriteStartObject();
                foreach (var property in properties.OrderBy(property => property.Name, StringComparer.Ordinal))
                {
                    writer.WritePropertyName(property.Name);
                    WriteCanonical(writer, property.Value, depth + 1);
                }
                writer.WriteEndObject();
                break;
            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (var item in value.EnumerateArray()) WriteCanonical(writer, item, depth + 1);
                writer.WriteEndArray();
                break;
            default:
                value.WriteTo(writer);
                break;
        }
        writer.Flush();
        if (writer.BytesCommitted > ModelProtocol.MaxArgumentsBytes) throw InvalidApproval();
    }

    private static string Digest(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    private static AgentStateException InvalidApproval() => new("RUN_INVALID_APPROVAL");
}
