using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Xml;
using System.Xml.Linq;
using FgoPet.Core.Dialogue;
using FgoPet.Kernel.Agent;
using Microsoft.Extensions.Logging;

namespace FgoPet.App.Dialogue;

/// <summary>Safe projection accounting and budget rejection metadata. Never emits content, raw identifiers or exception details.</summary>
internal static class ConversationContextDiagnostics
{
    private static readonly string[] Categories = ["persona", "history", "summary", "memory", "tools", "other"];
    private const string NativeMemorySource = "firstparty.memory:confirmed-memory:confirmed-memory";
    private const int MaxReferences = 64;

    internal static void RecordInputBudgetRejection(ILogger? logger, string phase, TokenMeasurement measurement,
        int inputTokenLimit, int messageCount, int toolCount, int nextPrimaryAttempt)
    {
        try
        {
            if (logger?.IsEnabled(LogLevel.Warning) != true) return;
            var safePhase = phase is "ordinary_initial" or "ordinary_read_continuation" ? phase : "other";
            var requestHash = measurement.Fingerprint is { Length: 64 } fingerprint && fingerprint.All(Uri.IsHexDigit)
                ? fingerprint : "unavailable";
            var countKind = measurement.Kind switch
            {
                TokenCountKind.Estimated => "estimated",
                TokenCountKind.ProviderUsage => "provider_usage",
                TokenCountKind.LocalReferenceEstimate => "local_reference_estimate",
                _ => "unavailable"
            };
            logger.LogWarning(new EventId(4103, "ConversationInputBudgetRejected"),
                "Input rejected {TimestampUtc:O} phase={Phase} code={ErrorCode} input={MeasuredInputTokens} limit={InputTokenLimit} kind={CountKind} messages={MessageCount} tools={ToolCount} nextAttempt={NextPrimaryAttempt} request={RequestHash}",
                DateTimeOffset.UtcNow, safePhase, "DIALOGUE_INPUT_BUDGET_EXCEEDED", measurement.InputTokens, inputTokenLimit,
                countKind, messageCount, toolCount, nextPrimaryAttempt, requestHash);
        }
        catch (Exception)
        {
            // A failing diagnostic sink cannot bypass the guard or expose its private exception.
        }
    }

    internal static void Record(ILogger? logger, string runId, int step, string phase,
        StepEnvironment environment, Func<StepEnvironment, int> measure)
    {
        try
        {
            if (logger?.IsEnabled(LogLevel.Debug) != true) return;
            var now = DateTimeOffset.UtcNow;
            var runHash = Hash(runId);
            var safePhase = phase is "before_compact" or "after_compact" or "before" or "after" or "prepare" or "dispatch"
                ? phase : "other";
            var messages = environment.Messages.IsDefault ? ImmutableArray<ModelMessage>.Empty : environment.Messages;
            var tools = environment.Tools.IsDefault ? [] : environment.Tools;
            var groups = Categories.ToDictionary(category => category, _ => ImmutableArray.CreateBuilder<ModelMessage>(), StringComparer.Ordinal);
            var references = new List<Reference>();
            var referencesTruncated = false;
            var personaCorePresent = false;
            foreach (var message in messages)
            {
                var envelope = ReadEnvelope(message.Content);
                var category = Category(message, envelope);
                groups[category].Add(message);
                if (category == "persona" && envelope is { Source: "servant_core", Payload.Length: > 0 }) personaCorePresent = true;
                CollectReferences(category, envelope, references, ref referencesTruncated);
            }
            logger.LogDebug(new EventId(4100, "ConversationContextTotal"),
                "Context {TimestampUtc:O} run={RunHash} phase={Phase} step={Step} messages={TotalMessageCount} input={TotalInputTokens} tools={ToolDefinitionCount} personaCore={PersonaCorePresent} basis={MeasurementBasis} metadataTruncated={MetadataTruncated}",
                now, runHash, safePhase, Math.Max(0, step), messages.Length, Measure(new(messages, tools), measure), tools.Length,
                personaCorePresent, "independent_envelopes_not_additive", referencesTruncated);

            foreach (var category in Categories)
            {
                var subset = groups[category].ToImmutable();
                var definitions = category == "tools" ? tools : [];
                // An empty sentinel makes definition-only accounting a valid independent envelope.
                // Counts refer to real messages, and category estimates must not be summed.
                var input = subset.IsEmpty && definitions.IsEmpty ? 0 : Measure(new(
                    subset.IsEmpty ? [new(ModelMessageRole.System, string.Empty, [])] : subset, definitions), measure);
                logger.LogDebug(new EventId(4101, "ConversationContextCategory"),
                    "Context category {TimestampUtc:O} run={RunHash} phase={Phase} step={Step} category={Category} messages={MessageCount} input={InputTokens}",
                    now, runHash, safePhase, Math.Max(0, step), category, subset.Length, input);
            }
            foreach (var reference in references)
                logger.LogDebug(new EventId(4102, "ConversationContextReference"),
                    "Context reference {TimestampUtc:O} run={RunHash} phase={Phase} step={Step} kind={ReferenceKind} hash={ReferenceHash} version={Version} revision={Revision} sequence={Sequence}",
                    now, runHash, safePhase, Math.Max(0, step), reference.Kind, reference.Hash,
                    reference.Version, reference.Revision, reference.Sequence);
        }
        catch (Exception)
        {
            // Diagnostics must neither fail a run nor turn an exception's private message into a log.
        }
    }

    private static int? Measure(StepEnvironment environment, Func<StepEnvironment, int> measure)
    {
        try { var value = measure(environment); return value >= 0 ? value : null; }
        catch (Exception) { return null; }
    }

    private static string Category(ModelMessage message, Envelope? envelope)
    {
        if (message.Role == ModelMessageRole.Tool || !message.ToolCalls.IsDefaultOrEmpty) return "tools";
        if (envelope is null) return "other";
        if (message.Role == ModelMessageRole.System && envelope.Tag == "persona"
            && (envelope.Source == "servant_core" || envelope.Source.StartsWith("appearance:", StringComparison.Ordinal))) return "persona";
        if (envelope.Tag != "data") return "other";
        if (envelope.Source is "conversation_summary" or "run_context_summary" or "previous_summary") return "summary";
        if (message.Role == ModelMessageRole.System && (envelope.Source == NativeMemorySource
            || envelope.Source.StartsWith("memory:", StringComparison.Ordinal))) return "memory";
        if (envelope.Source.StartsWith("history:", StringComparison.Ordinal)
            || envelope.Source.StartsWith("history/source:", StringComparison.Ordinal)
            || envelope.Source.StartsWith("recalled:", StringComparison.Ordinal)) return "history";
        return "other";
    }

    private static Envelope? ReadEnvelope(string content)
    {
        if (string.IsNullOrEmpty(content) || content[0] != '<' || content.Length > ModelProtocol.MaxContentBytes) return null;
        try
        {
            using var reader = XmlReader.Create(new StringReader(content), new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Prohibit,
                XmlResolver = null,
                MaxCharactersInDocument = ModelProtocol.MaxContentBytes,
                MaxCharactersFromEntities = 1024
            });
            var root = XElement.Load(reader);
            if (root.Name.Namespace != XNamespace.None || root.Name.LocalName is not ("data" or "persona")
                || root.HasElements || root.Attributes().Count() != 1 || root.Attribute("source") is not { } source
                || source.Value.Length > 1024) return null;
            var text = root.Value.TrimStart('\r', '\n');
            var start = text.IndexOf('\n');
            return new(root.Name.LocalName, source.Value, start >= 0 ? text[(start + 1)..].Trim() : string.Empty);
        }
        catch (Exception error) when (error is XmlException or ArgumentException or InvalidOperationException) { return null; }
    }

    private static void CollectReferences(string category, Envelope? envelope, List<Reference> references, ref bool truncated)
    {
        if (envelope is null) return;
        var source = envelope.Source;
        if (category == "history")
        {
            if (source.StartsWith("history/source:", StringComparison.Ordinal))
            {
                var suffix = source["history/source:".Length..];
                var delimiter = suffix.LastIndexOf(':');
                if (delimiter > 0 && suffix[(delimiter + 1)..] is "User" or "Assistant" or "System")
                    Add(new("message", Hash(suffix[..delimiter])), references, ref truncated);
            }
            else if (source.StartsWith("recalled:", StringComparison.Ordinal))
                Add(new("message_reference", Hash(source["recalled:".Length..])), references, ref truncated);
            else if (int.TryParse(source.AsSpan("history:".Length), out var sequence) && sequence > 0)
                Add(new("message_sequence", null, Sequence: sequence), references, ref truncated);
        }
        else if (category == "memory")
        {
            if (source.StartsWith("memory:", StringComparison.Ordinal))
                Add(new("memory", Hash(source["memory:".Length..])), references, ref truncated);
            else ReadMemoryReferences(envelope.Payload, references, ref truncated);
        }
    }

    private static void ReadMemoryReferences(string payload, List<Reference> references, ref bool truncated)
    {
        try
        {
            using var document = JsonDocument.Parse(payload, new JsonDocumentOptions { MaxDepth = 8 });
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("memories", out var memories)
                || memories.ValueKind != JsonValueKind.Array) return;
            long? revision = root.TryGetProperty("revision", out var rev) && rev.TryGetInt64(out var value) && value >= 0 ? value : null;
            foreach (var memory in memories.EnumerateArray())
            {
                if (references.Count == MaxReferences) { truncated = true; break; }
                if (memory.ValueKind != JsonValueKind.Object || !memory.TryGetProperty("memoryId", out var id)
                    || id.ValueKind != JsonValueKind.String || id.GetString() is not { Length: > 0 and <= 256 } identifier) continue;
                int? version = memory.TryGetProperty("version", out var versionNode)
                    && versionNode.TryGetInt32(out var numeric) && numeric >= 0 ? numeric : null;
                Add(new("memory", Hash(identifier), version, revision), references, ref truncated);
            }
        }
        catch (Exception error) when (error is JsonException or InvalidOperationException or FormatException) { }
    }

    private static void Add(Reference reference, List<Reference> references, ref bool truncated)
    {
        if (references.Count < MaxReferences) references.Add(reference);
        else truncated = true;
    }
    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    private sealed record Envelope(string Tag, string Source, string Payload);
    private sealed record Reference(string Kind, string? Hash, int? Version = null, long? Revision = null, int? Sequence = null);
}
