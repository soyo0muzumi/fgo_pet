using FgoPet.Extensibility;

namespace FgoPet.Kernel.Agent;

public enum ToolPolicyDecision { Allow, Ask, Deny }
public interface IToolPolicy
{
    ToolPolicyDecision Decide(ToolExecutionRequest request, RegisteredTool tool);
}

/// <summary>Trusted host configuration. Approval still binds one exact call; this is not a model-controlled flag.</summary>
public sealed class AskCommandPolicy : IToolPolicy
{
    public ToolPolicyDecision Decide(ToolExecutionRequest request, RegisteredTool tool)
        => tool.Descriptor.Effect == ToolEffect.Command ? ToolPolicyDecision.Ask : ToolPolicyDecision.Allow;
}
