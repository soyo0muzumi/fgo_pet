using FgoPet.Extensibility;
using FgoPet.Kernel.Agent;

namespace FgoPet.App.Dialogue;

/// <summary>Only bounded scene commands are reachable through explicit per-call human approval.</summary>
public sealed class NativeSceneToolPolicy : IToolPolicy
{
    public ToolPolicyDecision Decide(ToolExecutionRequest request, RegisteredTool tool)
    {
        if (tool.Descriptor.Effect is ToolEffect.ReadOnly or ToolEffect.Proposal) return ToolPolicyDecision.Allow;
        if (tool.Descriptor.Effect != ToolEffect.Command) return ToolPolicyDecision.Deny;
        return (tool.PluginId, tool.Descriptor.Name) switch
        {
            ("reminders", "reminder.create" or "reminder.cancel") => ToolPolicyDecision.Ask,
            ("firstparty.focus", "focus.resume") => ToolPolicyDecision.Ask,
            ("browser", "browser.click" or "browser.fill") => ToolPolicyDecision.Ask,
            _ => ToolPolicyDecision.Deny
        };
    }
}
