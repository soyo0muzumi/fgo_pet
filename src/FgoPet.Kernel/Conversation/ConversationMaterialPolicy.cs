using FgoPet.Core.Dialogue;
using FgoPet.Kernel.Agent;

namespace FgoPet.App.Dialogue;

/// <summary>Reuses the existing resolved conversation intent; no material-specific text classifier.</summary>
internal static class ConversationMaterialPolicy
{
    internal const string OrdinarySource = "topic-material";
    private const string NativePrefix = "<data source=\"firstparty.topic-material:topic-material:material\">";
    internal static bool Allows(PromptContext context)
    {
        var intent = MashExpressionV02.Select(context).Intent;
        return intent.FollowUp != "none" && intent.Act is not ("close" or "greet" or "acknowledge" or "clarify");
    }
    internal static bool IsNativeMaterial(ModelMessage message) => message.Role == ModelMessageRole.System
        && message.Content.StartsWith(NativePrefix, StringComparison.Ordinal);
}
