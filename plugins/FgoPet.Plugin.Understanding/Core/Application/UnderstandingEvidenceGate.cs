using FgoPet.Understanding.Contracts;

namespace FgoPet.Understanding;

internal static class UnderstandingEvidenceGate
{
    internal static string? Rejection(UnderstandingEvidenceSupport? support) => support switch
    {
        UnderstandingEvidenceSupport.Supported => null,
        UnderstandingEvidenceSupport.Partial => "UNDERSTANDING_EVIDENCE_PARTIAL",
        UnderstandingEvidenceSupport.Unsupported => "UNDERSTANDING_EVIDENCE_UNSUPPORTED",
        UnderstandingEvidenceSupport.Contradicted => "UNDERSTANDING_EVIDENCE_CONTRADICTED",
        _ => "UNDERSTANDING_EVIDENCE_VALIDATION_REQUIRED"
    };
}
