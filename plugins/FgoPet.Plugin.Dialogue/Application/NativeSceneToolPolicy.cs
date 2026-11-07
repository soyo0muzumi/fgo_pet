using FgoPet.Extensibility;
using FgoPet.Kernel.Agent;

namespace FgoPet.App.Dialogue;

/// <summary>Named scene/browser commands require review; the separately opted-in append-only sticker owner enforces its Web grant.</summary>
public sealed class NativeSceneToolPolicy : IToolPolicy
{
    public ToolPolicyDecision Decide(ToolExecutionRequest request, RegisteredTool tool)
    {
        if (tool.Descriptor.Effect is ToolEffect.ReadOnly or ToolEffect.Proposal) return ToolPolicyDecision.Allow;
        if (tool.Descriptor.Effect != ToolEffect.Command) return ToolPolicyDecision.Deny;
        return (tool.PluginId, tool.Descriptor.Name) switch
        {
            ("firstparty.stickers", "sticker.add") => ToolPolicyDecision.Allow,
            ("reminders", "reminder.create" or "reminder.cancel") => ToolPolicyDecision.Ask,
            ("firstparty.focus", "focus.resume") => ToolPolicyDecision.Ask,
            ("browser", "browser.click" or "browser.fill") => ToolPolicyDecision.Ask,
            _ => ToolPolicyDecision.Deny
        };
    }
}
