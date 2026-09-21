using FgoPet.Core.Validation;

// Stage 2 / R5: sunk out of modules/dialogue/Contracts/ConversationContracts.cs.
// The key identifies which content (servant / package / appearance / persona /
// knowledge) a conversation ran against, and the memory module records it alongside
// memories. Keeping the type in the dialogue directory made memory depend on dialogue.
// Namespace is intentionally left as FgoPet.Core.Dialogue: it is still a dialogue
// concept, and changing it would ripple through 23 call sites for no layering gain.
namespace FgoPet.Core.Dialogue;

public sealed record ContentContextKey
{
    public ContentContextKey(
        string servantId,
        string packageId,
        string packageVersion,
        string appearanceId,
        string personaVersion,
        string knowledgeVersion)
    {
        ServantId = Phase3Validation.Id(servantId, nameof(servantId));
        PackageId = Phase3Validation.Id(packageId, nameof(packageId));
        PackageVersion = Phase3Validation.Id(packageVersion, nameof(packageVersion), 64);
        AppearanceId = Phase3Validation.Id(appearanceId, nameof(appearanceId));
        PersonaVersion = Phase3Validation.Id(personaVersion, nameof(personaVersion), 64);
        KnowledgeVersion = Phase3Validation.Id(knowledgeVersion, nameof(knowledgeVersion), 64);
    }

    public string ServantId { get; }
    public string PackageId { get; }
    public string PackageVersion { get; }
    public string AppearanceId { get; }
    public string PersonaVersion { get; }
    public string KnowledgeVersion { get; }
}
