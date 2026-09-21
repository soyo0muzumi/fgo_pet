using System.Text;
using System.Text.Json;
using FgoPet.Core.Dialogue;

namespace FgoPet.App.Dialogue;

/// <summary>Aggregates streamed tool_call deltas into one complete function call.</summary>
public sealed class ToolCallAggregator
{
    private readonly Dictionary<int, ToolCallBuilder> _calls = new();
    public bool SawToolCalls { get; private set; }

    public void Add(ChatToolCallDelta delta)
    {
        ArgumentNullException.ThrowIfNull(delta);
        SawToolCalls = true;
        var builder = _calls.TryGetValue(delta.Index, out var existing)
            ? existing
            : _calls[delta.Index] = new ToolCallBuilder();
        if (delta.Id is not null)
        {
            builder.Id = delta.Id;
        }

        if (delta.Name is not null)
        {
            builder.Name = delta.Name;
        }

        if (!string.IsNullOrEmpty(delta.ArgumentsDelta))
        {
            builder.Arguments.Append(delta.ArgumentsDelta);
        }
    }

    /// <summary>Returns the aggregated call when exactly one call and a tool_calls finish reason are present.</summary>
    public AggregatedToolCall? Complete(string? finishReason)
    {
        if (!string.Equals(finishReason, "tool_calls", StringComparison.Ordinal))
        {
            return null;
        }

        // Index-free providers emit fragments without an index field; they all
        // belong to a single call, so bucket them together.
        if (_calls.Count == 0)
        {
            return null;
        }

        var builders = _calls.OrderBy(pair => pair.Key).Select(pair => pair.Value).ToArray();
        if (builders.Length > 1)
        {
            return new AggregatedToolCall(string.Join(", ", builders.Select(b => b.Name ?? "?")), null, TooManyCalls: true);
        }

        var only = builders[0];
        return new AggregatedToolCall(only.Name ?? string.Empty, only.Arguments.ToString(), CallId: only.Id);
    }

    private sealed class ToolCallBuilder
    {
        public string? Id { get; set; }
        public string? Name { get; set; }
        public StringBuilder Arguments { get; } = new();
    }
}

public sealed record AggregatedToolCall(string Name, string? Arguments, bool TooManyCalls = false, string? CallId = null)
{
    public bool TryGetArguments(out JsonElement arguments)
    {
        arguments = default;
        if (TooManyCalls || string.IsNullOrWhiteSpace(Arguments))
        {
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(Arguments);
            arguments = document.RootElement.Clone();
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }
}
