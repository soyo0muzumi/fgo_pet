using System.Collections.Immutable;
using System.Text;
using FgoPet.Core.Dialogue;

namespace FgoPet.Kernel.Agent;

/// <summary>Buffers one complete bounded stream before its call batch can be admitted.</summary>
public sealed class ModelStepAggregator(ModelToolNameMap names)
{
    private readonly SortedDictionary<int, CallBuilder> _calls = new();
    private readonly StringBuilder _text = new();
    private int _contentBytes;
    private int _argumentsBytes;
    private bool _complete;
    private string? _finishReason;
    private ChatUsage? _usage;

    public void Add(ChatStreamChunk chunk)
    {
        ArgumentNullException.ThrowIfNull(chunk);
        if (_complete) throw new AgentProtocolException("MODEL_DATA_AFTER_COMPLETE");
        _contentBytes += Encoding.UTF8.GetByteCount(chunk.TextDelta);
        if (_contentBytes > ModelProtocol.MaxContentBytes) throw new AgentProtocolException("MODEL_CONTENT_TOO_LARGE");
        _text.Append(chunk.TextDelta);
        if (chunk.ToolCallDelta is { } delta)
        {
            if (delta.Index < 0 || delta.Index >= ModelProtocol.MaxCalls)
                throw new AgentProtocolException("MODEL_INVALID_CALL_INDEX");
            if (!_calls.TryGetValue(delta.Index, out var call)) _calls.Add(delta.Index, call = new());
            SetIdentity(ref call.Id, delta.Id);
            SetIdentity(ref call.Name, delta.Name);
            var bytes = Encoding.UTF8.GetByteCount(delta.ArgumentsDelta ?? string.Empty);
            call.Bytes += bytes;
            _argumentsBytes += bytes;
            if (call.Bytes > ModelProtocol.MaxArgumentsBytes || _argumentsBytes > ModelProtocol.MaxBatchArgumentsBytes)
                throw new AgentProtocolException("MODEL_ARGUMENTS_TOO_LARGE");
            call.Arguments.Append(delta.ArgumentsDelta);
        }
        if (chunk.FinishReason is { } finish)
        {
            if (_finishReason is not null && _finishReason != finish)
                throw new AgentProtocolException("MODEL_CONFLICTING_FINISH");
            _finishReason = finish;
        }
        if (chunk.Usage is not null) _usage = chunk.Usage;
        _complete = chunk.IsComplete;
    }

    public ModelStepResponse Complete(IReadOnlySet<string>? previousCallIds = null)
    {
        var calls = ImmutableArray.CreateBuilder<ModelToolCall>(_calls.Count);
        foreach (var (index, call) in _calls)
        {
            if (index != calls.Count) throw new AgentProtocolException("MODEL_INVALID_CALL_INDEX");
            if (call.Id is null || call.Name is null) throw new AgentProtocolException("MODEL_INVALID_CALL");
            var resolved = names.TryResolve(call.Name, out var canonical);
            calls.Add(new(call.Id, resolved ? canonical : call.Name, call.Arguments.ToString()) { IsResolved = resolved });
        }
        var response = new ModelStepResponse(new(ModelMessageRole.Assistant, _text.ToString(), calls.ToImmutable()),
            _finishReason ?? string.Empty, _complete, _usage);
        ModelProtocol.ValidateResponse(response, previousCallIds ?? new HashSet<string>(StringComparer.Ordinal));
        return response;
    }

    private static void SetIdentity(ref string? stored, string? incoming)
    {
        if (incoming is null) return;
        if (stored is not null && stored != incoming) throw new AgentProtocolException("MODEL_CONFLICTING_CALL_IDENTITY");
        stored = incoming;
    }

    private sealed class CallBuilder
    {
        public string? Id;
        public string? Name;
        public int Bytes;
        public StringBuilder Arguments { get; } = new();
    }
}
