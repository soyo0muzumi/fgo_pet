using System.Collections.Immutable;

namespace FgoPet.Platform.Processes;

public sealed record BoundedProcessRequest(string Executable, ImmutableArray<string> Arguments,
    string WorkingDirectory, TimeSpan Timeout, int MaxOutputBytes = 16384, long MaxMemoryBytes = 512 * 1024 * 1024,
    int MaxProcesses = 16)
{
    /// <summary>Trusted owner revalidation, called before process creation and before its first instruction.</summary>
    public Action? BeforeStart { get; init; }
    public string? Correlation { get; init; }
    public ReadOnlyMemory<byte> StandardInput { get; init; }
}
public enum BoundedProcessStage { InputReceived, Queued, Launching, Started, Completed, TimedOut, Cancelled, Failed }
public sealed record BoundedProcessNotification(DateTimeOffset Timestamp, string Correlation,
    BoundedProcessStage Stage, int? ExitCode = null, string? ErrorCode = null);
public interface IBoundedProcessDiagnostics { void Record(BoundedProcessNotification notification); }
public enum BoundedProcessExit { Completed, TimedOut, Cancelled, LaunchFailed, Unconfirmed }
/// <summary>Private owner result. Output must never be copied to public runtime diagnostics.</summary>
public sealed record BoundedProcessResult(bool Started, BoundedProcessExit Exit, int? ExitCode,
    string StandardOutput, string StandardError, bool Truncated, string? ErrorCode = null);
public interface IBoundedProcessRunner
{
    ValueTask<BoundedProcessResult> RunAsync(BoundedProcessRequest request, CancellationToken token);
}
