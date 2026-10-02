using FgoPet.Core.Dialogue;
using FgoPet.Core.Packs;

namespace FgoPet.Infrastructure.Packs;

public sealed record ContentBinding(
    ContentContextKey Context,
    PersonaBundle? Persona,
    IReadOnlyList<KnowledgeEntry> Knowledge,
    IReadOnlyList<string> AppliedLayers,
    string PersonaHash,
    string KnowledgeHash);
