using System.Text.Json;

namespace FgoPet.Core.Dialogue;

/// <summary>Capability-owned derived data participates in explicit local privacy operations.
/// The host owns maintenance, staging-path validation and replacement/rollback; participants own their data.</summary>
public interface IDerivedConversationData
{
    void ForgetConversation(string conversationId, string roleId);
    void ForgetAll();
    JsonElement Export();
    void NormalizeRestoredCopy(string validatedStagingDatabasePath);
}
