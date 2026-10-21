using FgoPet.Extensibility;

namespace FgoPet.Core.Dialogue;

/// <summary>Dialogue-owned live scope. Consumers cannot select or grant another role/project through payloads.</summary>
public interface IConversationScopeAuthority
{
    ConversationScope? Current { get; }
    bool IsActive(ToolScope scope);
}
