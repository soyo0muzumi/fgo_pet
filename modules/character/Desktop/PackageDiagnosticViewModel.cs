using System.IO;
using FgoPet.Core.Packs;
using FgoPet.Infrastructure.Diagnostics;

namespace FgoPet.App.Servants;

/// <summary>
/// A user-visible package error with stable code and a redacted location: only the
/// relative path is shown, never an absolute source path.
/// </summary>
public sealed class PackageDiagnosticViewModel
{
    public PackageDiagnosticViewModel(PackFailure failure)
    {
        Code = failure.Code;
        Heading = $"错误 {Code}";
        Text = Compose(failure);
    }

    public PackErrorCode Code { get; }

    public string Heading { get; }

    public string Text { get; }

    private static string Compose(PackFailure failure)
    {
        var relative = SafeRelativePath(failure.RelativePath);
        return relative is null
            ? $"{failure.Code}"
            : $"{failure.Code} {relative}";
    }

    private static string? SafeRelativePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.Length > 256 || path.Any(char.IsControl))
        {
            return null;
        }

        var normalized = path.Replace('\\', '/');
        if (normalized.StartsWith('/') ||
            Path.IsPathFullyQualified(path) ||
            normalized.Split('/').Any(segment => segment == ".."))
        {
            return null;
        }

        var redacted = ResourceDiagnostics.Redact(normalized);
        return redacted.Contains(ResourceDiagnostics.RedactionToken, StringComparison.Ordinal)
            ? null
            : redacted;
    }
}
