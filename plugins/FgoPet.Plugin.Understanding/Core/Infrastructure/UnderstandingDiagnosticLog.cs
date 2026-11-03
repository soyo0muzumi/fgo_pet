using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FgoPet.Understanding.Contracts;
using Microsoft.Data.Sqlite;

namespace FgoPet.Understanding;

/// <summary>Local, bounded metadata only. A failed diagnostic write never affects dispatch.</summary>
public sealed class UnderstandingDiagnosticLog(string path, TimeProvider? clock = null)
{
    public const int MaximumBytes = 2 * 1024 * 1024;
    private readonly object _gate = new();
    private readonly string _session = Guid.NewGuid().ToString("N");
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private readonly Dictionary<(string Correlation,string Stage),(string? Code,long At)> _recent=[];

    public void Record(UnderstandingDiagnostic value)
    {
        try
        {
            // Producers emit closed stages/codes, never exception text or model/user payloads.
            if (value.Stage.Length is < 1 or > 80 || !value.Stage.All(c => c is >= 'a' and <= 'z' or '_')) return;
            var code = value.Code is null ? null : value.Code.Length is > 0 and <= 96 &&
                value.Code.All(c => c is >= 'A' and <= 'Z' or >= '0' and <= '9' or '_') ? value.Code : "UNDERSTANDING_DIAGNOSTIC_REDACTED";
            var correlation = value.Correlation.Length == 16 && value.Correlation.All(Uri.IsHexDigit)
                ? value.Correlation : UnderstandingDiagnostics.Hash(value.Correlation);
            var bytes = JsonSerializer.SerializeToUtf8Bytes(new { value.At, Session = _session, Correlation = correlation, value.Stage, Code = code });
            lock (_gate)
            {
                var key=(correlation,value.Stage);var now=_clock.GetTimestamp();
                // Preserve the failure and the next recovery path, even within the sampling interval.
                if(value.Stage.EndsWith("_failed",StringComparison.Ordinal))_recent.Clear();
                if(_recent.TryGetValue(key,out var previous)&&previous.Code==code&&_clock.GetElapsedTime(previous.At,now)<TimeSpan.FromMinutes(1))return;
                Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
                if (File.Exists(path) && new FileInfo(path).Length + bytes.Length + 1 > MaximumBytes)
                    File.Move(path, path + ".previous", overwrite: true);
                using var output = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read);
                output.Write(bytes);
                output.WriteByte((byte)'\n');
                output.Flush();
                if(_recent.Count>=512)_recent.Clear();
                _recent[key]=(code,now);
            }
        }
        catch (Exception) { /* Logging must neither grant admission nor stop the worker. */ }
    }
}

internal static class UnderstandingDiagnostics
{
    internal static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)))[..16];

    internal static void Write(Action<UnderstandingDiagnostic>? sink, TimeProvider clock, string correlation, string stage, string? code = null)
    {
        try { sink?.Invoke(new(clock.GetUtcNow(), Hash(correlation), stage, code)); }
        catch (Exception) { /* Diagnostics cannot change the operation being observed. */ }
    }

    internal static string FailureCode(Exception error) => error switch
    {
        OperationCanceledException => "UNDERSTANDING_CANCELLED",
        SqliteException { SqliteErrorCode: 5 or 6 } => "UNDERSTANDING_STORE_BUSY",
        SqliteException => "UNDERSTANDING_STORE_FAILED",
        IOException => "UNDERSTANDING_IO_FAILED",
        UnauthorizedAccessException => "UNDERSTANDING_ACCESS_DENIED",
        InvalidOperationException { Message: "UNDERSTANDING_SOURCE_UNAVAILABLE" } => "UNDERSTANDING_SOURCE_UNAVAILABLE",
        InvalidOperationException { Message: "UNDERSTANDING_SCAN_INVALID" } => "UNDERSTANDING_SCAN_INVALID",
        _ => "UNDERSTANDING_WORKER_FAILED"
    };
}
