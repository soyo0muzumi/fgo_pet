using FgoPet.Extensibility;

namespace FgoPet.Agent.Tests.TestDoubles;

public sealed class RecordingToolProvider(string name = "fixture.read", ToolEffect effect = ToolEffect.ReadOnly,
    string schema = "{\"type\":\"object\"}") : IToolProvider
{
    public ToolDescriptor Descriptor { get; } = new(name, "Synthetic tool", schema, effect);
    public List<ToolInvocation> Invocations { get; } = [];
    public Func<ToolInvocation, CancellationToken, ValueTask<ToolResult>> Handler { get; set; }
        = (invocation, _) => ValueTask.FromResult(new ToolResult(true, invocation.Arguments.Clone()));

    public ValueTask<ToolResult> InvokeAsync(ToolInvocation invocation, CancellationToken cancellationToken)
    {
        Invocations.Add(invocation);
        return Handler(invocation, cancellationToken);
    }
}
