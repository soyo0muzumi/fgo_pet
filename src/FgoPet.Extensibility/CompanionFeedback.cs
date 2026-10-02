using FgoPet.Core.Portraits;

namespace FgoPet.Extensibility;

public sealed record CompanionContentScope(string PackageId, string PackageVersion, string AppearanceId, string RoleId);
public sealed record CompanionFeedback(string Text, ExpressionSemantic Expression);

/// <summary>Resolves plain content and a semantic expression; never issues renderer commands.</summary>
public interface ICompanionFeedbackProvider
{
    Task<CompanionFeedback?> GetFeedbackAsync(CompanionSignal signal, CompanionContentScope scope, CancellationToken cancellationToken);
}
