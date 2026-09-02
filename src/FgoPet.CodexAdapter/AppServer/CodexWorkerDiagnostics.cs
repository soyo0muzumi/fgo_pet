using System.Security.Cryptography;
using System.Text;

namespace FgoPet.CodexAdapter.AppServer;

/// <summary>
/// Best-effort diagnostics for the dispatch worker. Values are intentionally
/// constrained to stage/outcome/error-code data; prompts, credentials and
/// filesystem paths never enter this log.
/// </summary>
public interface ICodexWorkerDiagnostics
{
    void Record(string stage, string outcome, string? errorCode = null, string? dispatchRequestId = null);
}

public sealed class CodexWorkerDiagnostics : ICodexWorkerDiagnostics
{
    private static readonly object Gate = new();
    private readonly string _path;

    public CodexWorkerDiagnostics(string stateRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stateRoot);
        _path = Path.Combine(Path.GetFullPath(stateRoot), "CodexAdapter", "worker-diagnostics.log");
    }

    public void Record(string stage, string outcome, string? errorCode = null, string? dispatchRequestId = null)
    {
        try
        {
            var directory = Path.GetDirectoryName(_path);
            if (directory is null) return;
            var line = $"{DateTimeOffset.UtcNow:O} stage={SafeValue(stage)} outcome={SafeValue(outcome)} " +
                $"error={SafeValue(errorCode ?? "none")} dispatch={HashId(dispatchRequestId)}{Environment.NewLine}";
            lock (Gate)
            {
                Directory.CreateDirectory(directory);
                File.AppendAllText(_path, line, Encoding.UTF8);
            }
        }
        catch
        {
            // Diagnostics must never change dispatch behavior.
        }
    }

    public static string ErrorCode(Exception error)
    {
        ArgumentNullException.ThrowIfNull(error);
        var message = error.Message.Trim();
        return message switch
        {
            "codex_not_installed" or "codex_executable_invalid" or "codex_start_failed" or
            "codex_resume_start_failed" or "codex_rpc_closed" or "codex_rpc_unavailable" or
            "codex_rpc_rejected" or "codex_turn_missing" or "codex_rpc_frame_too_large" or
            "target_not_registered" or "dispatch_ack_rejected" or "event_rejected" or
            "dispatch_identity_mismatch" or "dispatch_journal_full" or "connection_or_state_unavailable" => message,
            _ when error is OperationCanceledException => "cancelled",
            _ when error is TimeoutException => "timeout",
            _ when error is UnauthorizedAccessException => "unauthorized",
            _ when error is InvalidDataException => "invalid_data",
            _ when error is ArgumentException => "invalid_argument",
            _ when error is System.ComponentModel.Win32Exception => "win32",
            _ when error is System.Text.Json.JsonException => "json",
            _ when error is IOException => "io",
            _ => "unexpected",
        };
    }

    private static string HashId(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "none";
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        return Convert.ToHexString(hash)[..12];
    }

    private static string SafeValue(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "none";
        var builder = new StringBuilder(Math.Min(value.Length, 64));
        foreach (var character in value[..Math.Min(value.Length, 64)])
            builder.Append(char.IsLetterOrDigit(character) || character is '_' or '-' or '.' ? character : '_');
        return builder.ToString();
    }
}

internal sealed class NullCodexWorkerDiagnostics : ICodexWorkerDiagnostics
{
    public static NullCodexWorkerDiagnostics Instance { get; } = new();

    public void Record(string stage, string outcome, string? errorCode = null, string? dispatchRequestId = null)
    {
    }
}
