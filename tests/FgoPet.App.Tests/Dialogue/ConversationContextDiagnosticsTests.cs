using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FgoPet.App.Dialogue;
using FgoPet.Core.Dialogue;
using FgoPet.Extensibility;
using FgoPet.Infrastructure.Providers;
using FgoPet.Kernel.Agent;
using Microsoft.Extensions.Logging;
using Xunit;

namespace FgoPet.App.Tests.Dialogue;

public sealed class ConversationContextDiagnosticsTests
{
    private const string Secret = "synthetic-secret-DO-NOT-LOG";
    private const string RunId = "synthetic-private-run";
    private const string MessageId = "synthetic-private-message";
    private const string MemoryId = "synthetic-private-memory";
    private static ModelMessage Message(ModelMessageRole role, string content) => new(role, content, []);
    private static readonly ModelRouteKey Route = new("fixture", "endpoint", "model", "1");
    private static readonly RequestTokenMeter Meter = new();
    private static int Measure(StepEnvironment environment) => Meter.Measure(Route,
        ChatRequest.CreateAgent("fixture", "conversation", environment.Messages,
            tools: environment.Tools.IsEmpty ? null : environment.Tools.Select(tool => new ChatToolDefinition(
                ModelToolNameMap.GetWireName(tool.Name), tool.Description, tool.Parameters.GetRawText())).ToArray())).InputTokens;

    private static StepEnvironment Environment() => new(
    [
        Message(ModelMessageRole.System, PromptInjectionGuard.WrapPersona("servant_core", Secret)),
        Message(ModelMessageRole.User, PromptInjectionGuard.Wrap($"history/source:{MessageId}:User", Secret)),
        Message(ModelMessageRole.System, PromptInjectionGuard.Wrap("conversation_summary", Secret)),
        Message(ModelMessageRole.System, PromptInjectionGuard.Wrap("firstparty.memory:confirmed-memory:confirmed-memory",
            JsonSerializer.Serialize(new { revision = 12, memories = new[] { new { memoryId = MemoryId, version = 7, text = Secret, projectId = Secret } }, truncated = false }))),
        new(ModelMessageRole.Assistant, "", [new("synthetic-private-call", "fixture.read", "{\"input\":\"" + Secret + "\"}")]),
        new(ModelMessageRole.Tool, Secret, [], "synthetic-private-call"),
        Message(ModelMessageRole.System, PromptInjectionGuard.Wrap("https://secret.test/C:/private/" + Secret, Secret)),
        Message(ModelMessageRole.User, PromptInjectionGuard.WrapCurrentUserMessage(Secret))
    ], [new ToolDescriptor("fixture.read", Secret, "{\"type\":\"object\"}", ToolEffect.ReadOnly)]);

    [Fact]
    public void Disabled_debug_or_null_logger_never_invokes_measurement()
    {
        var logger = new CaptureLogger(LogLevel.Information);
        var measured = 0;
        ConversationContextDiagnostics.Record(logger, RunId, 1, "before_compact", Environment(), _ => { measured++; return 1; });
        ConversationContextDiagnostics.Record(null, RunId, 1, "before_compact", Environment(), _ => { measured++; return 1; });
        Assert.Equal(0, measured);
        Assert.Empty(logger.Entries);
    }

    [Fact]
    public void Debug_records_safe_classification_metadata_and_independent_measurements_only()
    {
        var logger = new CaptureLogger();
        var environment = Environment();
        ConversationContextDiagnostics.Record(logger, RunId, 9, "before_compact", environment, Measure);
        var total = Assert.Single(logger.Entries.Where(entry => entry.Id == 4100));
        Assert.Equal(Measure(environment), total.Fields["TotalInputTokens"]);
        Assert.Equal(8, total.Fields["TotalMessageCount"]);
        Assert.Equal(1, total.Fields["ToolDefinitionCount"]);
        Assert.Equal(true, total.Fields["PersonaCorePresent"]);
        Assert.Equal(Hash(RunId), total.Fields["RunHash"]);
        Assert.Equal("before_compact", total.Fields["Phase"]);
        Assert.Equal(9, total.Fields["Step"]);
        Assert.Equal(TimeSpan.Zero, Assert.IsType<DateTimeOffset>(total.Fields["TimestampUtc"]).Offset);
        Assert.Equal("independent_envelopes_not_additive", total.Fields["MeasurementBasis"]);
        var categories = logger.Entries.Where(entry => entry.Id == 4101).ToDictionary(entry => (string)entry.Fields["Category"]!);
        Assert.Equal(6, categories.Count);
        foreach (var (category, count) in new[] { ("persona", 1), ("history", 1), ("summary", 1), ("memory", 1), ("tools", 2), ("other", 2) })
        {
            Assert.Equal(count, categories[category].Fields["MessageCount"]);
            Assert.True(Assert.IsType<int>(categories[category].Fields["InputTokens"]) > 0);
        }
        var memory = Assert.Single(logger.Entries.Where(entry => entry.Id == 4102 && Equals(entry.Fields["ReferenceKind"], "memory")));
        Assert.Equal(Hash(MemoryId), memory.Fields["ReferenceHash"]);
        Assert.Equal(7, memory.Fields["Version"]);
        Assert.Equal(12L, memory.Fields["Revision"]);
        var message = Assert.Single(logger.Entries.Where(entry => entry.Id == 4102 && Equals(entry.Fields["ReferenceKind"], "message")));
        Assert.Equal(Hash(MessageId), message.Fields["ReferenceHash"]);
        Assert.All(logger.Entries, entry => Assert.Equal(LogLevel.Debug, entry.Level));
        AssertNoContent(logger);
    }

    [Theory]
    [InlineData("<data source=\"history/source:bad\"><nested>synthetic-secret-DO-NOT-LOG</nested></data>")]
    [InlineData("<!DOCTYPE data [<!ENTITY x SYSTEM 'file:///C:/private/secret'>]><data source=\"servant_core\">&x;</data>")]
    [InlineData("<data source=\"broken")]
    public void Malformed_or_nested_wrappers_are_counted_as_other_without_metadata(string content)
    {
        var logger = new CaptureLogger();
        ConversationContextDiagnostics.Record(logger, RunId, 1, Secret,
            new([Message(ModelMessageRole.System, content)], []), Measure);
        Assert.DoesNotContain(logger.Entries, entry => entry.Id == 4102);
        var other = Assert.Single(logger.Entries.Where(entry => entry.Id == 4101 && Equals(entry.Fields["Category"], "other")));
        Assert.Equal(1, other.Fields["MessageCount"]);
        var total = Assert.Single(logger.Entries.Where(entry => entry.Id == 4100));
        Assert.Equal("other", total.Fields["Phase"]);
        Assert.Equal(false, total.Fields["PersonaCorePresent"]);
        AssertNoContent(logger);
    }

    [Fact]
    public void Invalid_memory_json_and_untrusted_persona_lookalike_never_claim_persona_or_leak_content()
    {
        var logger = new CaptureLogger();
        ConversationContextDiagnostics.Record(logger, RunId, 1, "after_compact", new(
        [
            Message(ModelMessageRole.User, PromptInjectionGuard.WrapPersona("servant_core", Secret)),
            Message(ModelMessageRole.System, PromptInjectionGuard.Wrap("firstparty.memory:confirmed-memory:confirmed-memory", "{broken" + Secret))
        ], []), Measure);
        Assert.Equal(false, Assert.Single(logger.Entries.Where(entry => entry.Id == 4100)).Fields["PersonaCorePresent"]);
        Assert.DoesNotContain(logger.Entries, entry => entry.Id == 4102);
        AssertNoContent(logger);
    }

    [Fact]
    public void Measurement_and_logger_failures_cannot_interrupt_the_run_or_log_exception_messages()
    {
        var logger = new CaptureLogger();
        ConversationContextDiagnostics.Record(logger, RunId, 1, "after_compact", Environment(), _ => throw new InvalidOperationException(Secret));
        Assert.Null(Assert.Single(logger.Entries.Where(entry => entry.Id == 4100)).Fields["TotalInputTokens"]);
        AssertNoContent(logger);
        Assert.Null(Record.Exception(() => ConversationContextDiagnostics.Record(new ThrowingLogger(), RunId, 1, "after_compact", Environment(), Measure)));
        Assert.Null(Record.Exception(() => ConversationContextDiagnostics.Record(logger, RunId, 1, "after_compact", new(default, default), Measure)));
    }

    [Fact]
    public void Tool_definitions_are_measured_without_claiming_a_real_tool_message()
    {
        var logger = new CaptureLogger();
        var environment = Environment() with { Messages = [Message(ModelMessageRole.User, Secret)] };
        ConversationContextDiagnostics.Record(logger, RunId, 1, "before_compact", environment, Measure);
        var tools = Assert.Single(logger.Entries.Where(entry => entry.Id == 4101 && Equals(entry.Fields["Category"], "tools")));
        Assert.Equal(0, tools.Fields["MessageCount"]);
        Assert.True(Assert.IsType<int>(tools.Fields["InputTokens"]) > 0);
        AssertNoContent(logger);
    }

    [Fact]
    public void Metadata_is_capped_and_untrusted_identifiers_are_only_hashed()
    {
        var logger = new CaptureLogger();
        var messages = Enumerable.Range(0, 80).Select(index => Message(ModelMessageRole.User,
            PromptInjectionGuard.Wrap($"history/source:{Secret}:https://secret.test/C:/private/{index}:User", Secret))).ToImmutableArray();
        ConversationContextDiagnostics.Record(logger, RunId, 1, "before", new(messages, []), Measure);
        Assert.Equal(64, logger.Entries.Count(entry => entry.Id == 4102));
        Assert.Equal(true, Assert.Single(logger.Entries.Where(entry => entry.Id == 4100)).Fields["MetadataTruncated"]);
        Assert.All(logger.Entries.Where(entry => entry.Id == 4102), entry =>
            Assert.Matches("^[A-F0-9]{64}$", Assert.IsType<string>(entry.Fields["ReferenceHash"])));
        AssertNoContent(logger);
    }

    private static void AssertNoContent(CaptureLogger logger)
    {
        var output = JsonSerializer.Serialize(logger.Entries);
        foreach (var forbidden in new[] { Secret, RunId, MessageId, MemoryId, "synthetic-private-call", "https://secret.test", "C:/private", "file:///" })
            Assert.DoesNotContain(forbidden, output, StringComparison.Ordinal);
    }
    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    private sealed record Entry(int Id, LogLevel Level, Dictionary<string, object?> Fields, string Formatted);
    private sealed class CaptureLogger(LogLevel minimum = LogLevel.Debug) : ILogger
    {
        public List<Entry> Entries { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => logLevel >= minimum;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            Assert.Null(exception);
            Entries.Add(new(eventId.Id, logLevel, ((IEnumerable<KeyValuePair<string, object?>>)state!).ToDictionary(pair => pair.Key, pair => pair.Value), formatter(state, exception)));
        }
    }
    private sealed class ThrowingLogger : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => throw new InvalidOperationException(Secret);
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) => throw new InvalidOperationException(Secret);
    }
}
