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

    // 卡 D（P0 审计 #6）：App.xaml.cs 是全仓唯一的异常落盘点，写盘前过 Redact。
    // 反向自测要求：把 Redact 换成恒等函数，下面这些断言必须失败。

    private static string BuildRealExceptionText()
    {
        try
        {
            try
            {
                throw new IOException($"cannot read {Path.Combine(Path.GetTempPath(), "prompt.txt")}");
            }
            catch (IOException inner)
            {
                throw new InvalidOperationException("startup probe failed", inner);
            }
        }
        catch (InvalidOperationException error)
        {
            return error.ToString();
        }
    }

    [Fact]
    public void Redact_strips_absolute_paths_from_a_full_exception_dump_but_keeps_it_diagnosable()
    {
        var dump = BuildRealExceptionText();

        // 前置条件：这份样例里确实有绝对路径，否则测试会假绿。
        Assert.Matches(@"[A-Za-z]:\\", dump);

        var redacted = ResourceDiagnostics.Redact(dump);

        Assert.DoesNotMatch(@"[A-Za-z]:\\", redacted);
        Assert.DoesNotContain("prompt.txt", redacted);
        // 脱敏不能过头：异常类型与 InnerException 层次必须留着，否则日志失去诊断价值。
        Assert.Contains("InvalidOperationException", redacted);
        Assert.Contains("IOException", redacted);
        Assert.Contains("startup probe failed", redacted);
        // Exception.ToString() 用 "--->" 标记内层异常；它必须留着，否则只剩最外层一片。
        Assert.Contains("--->", redacted);
    }

    [Fact]
    public void Redact_does_not_swallow_the_newline_in_front_of_a_path()
    {
        // (?:\s|^) 前缀会把路径前的空白一起吃掉；行首就是路径时会把换行吞掉，多行栈会被挤成一行。
        var text = "head\n/home/user/project/file.cs\ntail";

        var redacted = ResourceDiagnostics.Redact(text);

        Assert.Equal(text.Split('\n').Length, redacted.Split('\n').Length);
        Assert.Contains("head", redacted);
        Assert.Contains("tail", redacted);
    }
}