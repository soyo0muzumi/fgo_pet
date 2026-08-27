using System.IO;
using FgoPet.Core.Packs;
using FgoPet.Infrastructure.Diagnostics;
using Xunit;

namespace FgoPet.Infrastructure.Tests.Diagnostics;

public sealed class ResourceDiagnosticsTests
{
    [Fact]
    public void Compose_includes_whitelisted_fields_only()
    {
        var line = ResourceDiagnostics.Compose("official.mash", "1.0.0", PackErrorCode.AssetHashMismatch, "runtime/full_body.png");

        Assert.Contains("pkg=official.mash", line);
        Assert.Contains("ver=1.0.0", line);
        Assert.Contains("code=AssetHashMismatch", line);
        Assert.Contains("path=runtime/full_body.png", line);
    }

    [Fact]
    public void Redact_removes_absolute_windows_paths()
    {
        var secret = Path.Combine(Path.GetTempPath(), "prompt.txt");
        var redacted = ResourceDiagnostics.Redact(secret);

        Assert.DoesNotContain(secret, redacted);
    }

    [Fact]
    public void Redact_removes_api_key_like_values()
    {
        var sanitized = ResourceDiagnostics.Redact("Authorization: Bearer eyJtoken.api_key=s3cret");

        Assert.DoesNotContain("Bearer", sanitized);
        Assert.DoesNotContain("s3cret", sanitized);
    }

    [Fact]
    public void LogPackOutcome_emits_a_redacted_line()
    {
        var captured = new List<string>();
        var diagnostics = new ResourceDiagnostics(captured.Add);

        diagnostics.LogPackOutcome("official.mash", "1.0.0", PackErrorCode.AssetMissing, "previews/library.png");

        var line = Assert.Single(captured);
        Assert.Contains("pkg=official.mash", line);
        Assert.Contains("code=AssetMissing", line);
        Assert.DoesNotContain("C:", line);
    }

    [Fact]
    public void Redact_removes_credentials_embedded_in_a_relative_path()
    {
        // The API only accepts whitelisted fields; a credential smuggled through a path is redacted.
        var line = ResourceDiagnostics.Compose("official.mash", "1.0.0", PackErrorCode.ExpressionMappingInvalid, "runtime/expressions?token=abc123");

        Assert.DoesNotContain("abc123", line);
        Assert.Contains("[REDACTED]", line);
    }
}