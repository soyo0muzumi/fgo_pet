using System.IO;
using System.Security;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FgoPet.Kernel.Agent;

namespace FgoPet.Infrastructure.Dialogue;

/// <summary>Bounded operational failure metadata, including failures which could not be journaled.</summary>
internal sealed class NativeRunDiagnosticLog(string path)
{
    internal const int MaximumBytes = 64 * 1024;
    private readonly string file = Path.GetFullPath(path);
    private readonly object gate = new();
    private enum MeasurementBasis { IndependentEnvelopesNotAdditive }

    internal void RecordContext(ContextBudgetDiagnostic value)
    {
        if (!Enum.IsDefined(value.Stage) || !Enum.IsDefined(value.Point) || !Enum.IsDefined(value.Decision)
            || !Enum.IsDefined(value.CompactionKind) || !Enum.IsDefined(value.SummaryValidation)
            || value.CapacitySource is { } source && !Enum.IsDefined(source)) return;
        static int? Count(int? count) => count is >= 0 ? count : null;
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new {
            timestampUtc = DateTimeOffset.UtcNow, stage = value.Stage.ToString(),
            point = value.Point.ToString(), decision = value.Decision.ToString(),
            compactionKind = value.CompactionKind.ToString(),
            capacitySource = value.CapacitySource?.ToString(), windowTokens = Count(value.WindowTokens),
            inputBudget = Count(value.InputBudget), outputTokens = Count(value.OutputTokens),
            triggerTokens = Count(value.TriggerTokens), measuredInput = Count(value.MeasuredInput),
            messageCount = Count(value.MessageCount), toolCount = Count(value.ToolCount),
            toolDefinitionsInput = Count(value.ToolDefinitionsInput), systemInput = Count(value.SystemInput),
            transcriptInput = Count(value.TranscriptInput), mandatoryLowerBoundInput = Count(value.MandatoryLowerBoundInput),
            blockUtf8Bytes = Count(value.BlockUtf8Bytes), eligibleGroups = Count(value.EligibleGroups),
            auxiliaryBudgetReservations = Count(value.AuxiliaryBudgetReservations), step = Count(value.Step),
            summaryValidation = value.SummaryValidation.ToString(),
            summaryCharacters = Count(value.SummaryCharacters), summaryOutputTokens = Count(value.SummaryOutputTokens),
            auxiliaryInputBudget = Count(value.AuxiliaryInputBudget), auxiliaryOutputBudget = Count(value.AuxiliaryOutputBudget),
            measurementBasis = MeasurementBasis.IndependentEnvelopesNotAdditive.ToString()
        });
        Append(Path.Combine(Path.GetDirectoryName(file)!, "native-context-diagnostics.jsonl"), bytes);
    }

    internal void Record(AgentRunSnapshot snapshot, string stage, bool? persisted)
    {
        if (stage is not ("terminal_failure" or "runtime_exception")) return;
        var code = snapshot.ErrorCode is { Length: > 0 and <= 96 } value
            && value.All(c => c is >= 'A' and <= 'Z' or >= '0' and <= '9' or '_') ? value : "RUN_FAILED";
        var observed = stage == "terminal_failure";
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new {
            timestampUtc = DateTimeOffset.UtcNow, stage, status = snapshot.Status.ToString(), errorCode = code,
            runCorrelation = Hash(snapshot.Identity.RunId), modelRevisionCorrelation = Hash(snapshot.Identity.ModelRevision),
            statePersisted = persisted, step = observed ? snapshot.StepNumber : (int?)null,
            modelBudgetReservations = observed ? snapshot.ModelRequests : (int?)null,
            // Reservations are not evidence that HTTP bytes reached a provider or that a model was billed.
            transportOutcome = "Unknown" });
        Append(file, bytes);
    }

    private void Append(string target, byte[] bytes)
    {
        if (bytes.Length + 1 > MaximumBytes) return;
        try
        {
            lock (gate)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                if (File.Exists(target) && (File.GetAttributes(target) & FileAttributes.ReparsePoint) != 0) return;
                using var output = new FileStream(target, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Read);
                if (output.Length + bytes.Length + 1 > MaximumBytes) output.SetLength(0);
                output.Position = output.Length;
                output.Write(bytes); output.WriteByte((byte)'\n');
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or SecurityException)
        { /* Best-effort diagnostics never replace the original failure or trigger another request. */ }
    }

    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)))[..16];
}
