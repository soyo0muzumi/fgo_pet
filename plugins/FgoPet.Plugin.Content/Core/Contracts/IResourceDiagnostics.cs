using FgoPet.Core.Packs;

namespace FgoPet.Core.Diagnostics;

/// <summary>
/// Structural, redaction-first diagnostics: only IDs, versions, stable error codes, and
/// relative paths are ever accepted or emitted — never prompt text, chat content,
/// credentials, or absolute source paths.
/// </summary>
public interface IResourceDiagnostics
{
    void LogPackOutcome(string packageId, string? packageVersion, PackErrorCode code, string? relativePath);
}