using FgoPet.Core.Todo;

// Work-owned compatibility contracts. Keep the existing namespace during assembly migration.
namespace FgoPet.App.Dialogue;

/// <summary>
/// Confirmation used only by the legacy, user-operated proposal card. Calling this method
/// requires an explicit UI confirmation; parsing or receiving model output is not authorization.
/// It is deliberately separate from ITodoProposalReader and ITodoConversationPort. New chat
/// proposals use the scoped/versioned ITodoDraftWorkflow instead of this compatibility path.
/// </summary>
public interface ILegacyTodoProposalConfirmation
{
    TodoItem Confirm(TodoProposal proposal);
}

/// <summary>
/// Legacy card assembly boundary: parse the existing bounded proposal formats, then hand
/// confirmation to the card. Parse must validate input and must never write. The interface
/// does not grant model callers direct creation, execution, repository or draft-state access.
/// </summary>
public interface ILegacyTodoProposalPort : ILegacyTodoProposalConfirmation
{
    IReadOnlyList<TodoProposal> Parse(string modelResponse);
}
