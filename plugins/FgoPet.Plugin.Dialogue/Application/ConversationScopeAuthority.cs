using FgoPet.App.Runtime;
using FgoPet.Core.Dialogue;
using FgoPet.Extensibility;
using FgoPet.Infrastructure.Dialogue;

namespace FgoPet.App.Dialogue;

/// <summary>Conversation owns project/session selection and validates its current tool scope.</summary>
public sealed class ConversationScopeAuthority(Func<ConversationViewModel> currentModel, AppRuntime runtime,
    SqliteConversationRepository conversations) : IConversationScopeAuthority
{
    public ToolScope? ActiveConversation
    {
        get
        {
            var role = runtime.ActiveRole?.ServantId;
            if (role is null) return null;
            var id = conversations.ReadState($"LastActiveConversationId:{role}");
            if (id is null) return null;
            var conversation = conversations.GetConversation(id, role);
            if (conversation is not { IsArchived: false }) return null;
            var scope = new ToolScope(id, role, conversation.ProjectId);
            return IsActive(scope) ? scope : null;
        }
    }

    public ConversationScope? Current
    {
        get
        {
            var role = runtime.ActiveRole?.ServantId;
            if (role is null) return null;
            var model = currentModel();
            var conversation = conversations.GetConversation(model.CurrentConversationId, role);
            return new(role, conversation is not null ? conversation.ProjectId : model.ActiveServantId == role ? model.SessionContext.ProjectId : null);
        }
    }
    public bool IsActive(ToolScope scope)
    {
        if (runtime.ActiveRole?.ServantId != scope.RoleId) return false;
        var conversation = conversations.GetConversation(scope.ConversationId, scope.RoleId);
        return conversation is { IsArchived: false } && conversation.ProjectId == scope.ProjectId &&
            conversations.ReadState($"LastActiveConversationId:{scope.RoleId}") == scope.ConversationId;
    }
}
