using FgoPet.Extensibility;

namespace FgoPet.Core.Dialogue;

/// <summary>Dialogue-owned live scope. Consumers cannot select or grant another role/project through payloads.</summary>
public interface IConversationScopeAuthority
{
    ConversationScope? Current { get; }
    /// <summary>The existing active conversation, if any. Reading never creates a synthetic conversation.</summary>
    ToolScope? ActiveConversation => null;
    bool IsActive(ToolScope scope);
}
