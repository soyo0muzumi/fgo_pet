using System.Text;
using System.Text.RegularExpressions;
using FgoPet.Core.Diagnostics;
using FgoPet.Core.Packs;

namespace FgoPet.Infrastructure.Diagnostics;

/// <summary>
/// Emits only the whitelisted diagnostic fields (package ID, version, error code,
/// relative path), redacting anything that could carry credentials or absolute paths.
/// </summary>
public sealed partial class ResourceDiagnostics : IResourceDiagnostics
{
    public const string RedactionToken = "[REDACTED]";

    private readonly Action<string> _emit;

    public ResourceDiagnostics(Action<string> emit) => _emit = emit ?? throw new ArgumentNullException(nameof(emit));

    public void LogPackOutcome(string packageId, string? packageVersion, PackErrorCode code, string? relativePath)
        => _emit(Compose(packageId, packageVersion, code, relativePath));

    public static string Compose(string packageId, string? packageVersion, PackErrorCode code, string? relativePath)
    {
        var builder = new StringBuilder();
        builder.Append("pkg=").Append(Redact(Truncate(packageId, 128)));
        builder.Append(" ver=").Append(Redact(Truncate(packageVersion, 64)));
        builder.Append(" code=").Append(code);
        if (!string.IsNullOrWhiteSpace(relativePath))
        {
            builder.Append(" path=").Append(Redact(Truncate(relativePath, 256)));
        }

        return builder.ToString();
    }

    /// <summary>Removes credentials and absolute path material from a diagnostic string.</summary>
    public static string Redact(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        var result = CredentialRegex().Replace(text, RedactionToken);
        result = WindowsPathRegex().Replace(result, RedactionToken);
        result = UnixPathRegex().Replace(result, RedactionToken);
        return result;
    }

    private static string Truncate(string? text, int maxLength)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        return text.Length <= maxLength ? text : text[..maxLength];
    }

    [GeneratedRegex(@"(?i)(api[\w-]*key|token|secret|password|authorization)\s*[:=]\s*[^\s,;:]+")]
    private static partial Regex CredentialRegex();

    [GeneratedRegex(@"[A-Za-z]:\\[^\s:;" + "\"]+")]
    private static partial Regex WindowsPathRegex();

    [GeneratedRegex(@"(?:\s|^)(/(?:[^\s/]+/)*[^\s/]+)")]
    private static partial Regex UnixPathRegex();
}