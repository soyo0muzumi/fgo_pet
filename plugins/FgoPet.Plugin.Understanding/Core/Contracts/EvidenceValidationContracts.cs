using FgoPet.Extensibility;

namespace FgoPet.Understanding.Contracts;

/// <summary>Evidence support only; never approval, effective state, certainty or a replacement candidate.</summary>
public enum UnderstandingEvidenceSupport { Supported, Partial, Unsupported, Contradicted }

/// <summary>Host-bound assessment of an exact candidate and verified source set; missing evidence denies persistence.</summary>
public interface IUnderstandingEvidenceValidation
{
    UnderstandingEvidenceSupport? Assess(UnderstandingProposal proposal, IReadOnlyList<ConversationSourceSnapshot> verifiedRoots);
}
