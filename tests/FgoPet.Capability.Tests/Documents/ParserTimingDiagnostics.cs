using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using FgoPet.Platform.Processes;
using FgoPet.Platform.Windows.Processes;

namespace FgoPet.Capability.Tests.Documents;

// Explicit synthetic diagnostic mode only; normal tests retain their original helper and runner.
internal static class ParserTimingDiagnostics
{
    internal static (IBoundedProcessRunner Runner, string Helper) Create(string normalHelper,
        [CallerMemberName] string scenario = "")
    {
        var configured = Environment.GetEnvironmentVariable("FGO_PET_PARSER_TIMING_PROBE");
        if (string.IsNullOrEmpty(configured)) return (new WindowsBoundedProcessRunner(), normalHelper);
        var repository = new DirectoryInfo(AppContext.BaseDirectory);
        while (repository is not null && !File.Exists(Path.Combine(repository.FullName, "FgoPet.sln")))
            repository = repository.Parent;
        var salt = Environment.GetEnvironmentVariable("FGO_PET_PARSER_PHASE_SALT");
        var helper = Path.GetFullPath(configured);
        if (repository is null || !helper.StartsWith(Path.Combine(repository.FullName, "docs", "internal", "reports")
                + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            || Path.GetFileName(helper) != "FgoPet.DocumentParser.exe" || !File.Exists(helper)
            || salt is null || salt.Length != 32 || !salt.All(Uri.IsHexDigit))
            throw new InvalidOperationException("PARSER_PROBE_BINDING_REQUIRED");
        return (new TimedRunner(scenario), helper);
    }

    private sealed record Phase(string Process, string ProcessIdHash, string Stage,
        long Ticks, long PreviousWriteTicks, long Frequency);

    private sealed class TimedRunner(string scenario) : IBoundedProcessRunner
    {
        private readonly WindowsBoundedProcessRunner inner = new();
        private int sequence;
        private static readonly HashSet<string> KnownPhases = new(StringComparer.Ordinal) {
            "managed_entry", "stdin_begin", "stdin_eof", "pdf_open_begin", "pdf_open_end",
            "extract_begin", "extract_end", "result_ready", "stdout_emitted", "failure_ready", "failure_emitted"
        };
        public async ValueTask<BoundedProcessResult> RunAsync(BoundedProcessRequest request, CancellationToken token)
        {
            var call = Interlocked.Increment(ref sequence);
            var beforeCall = Stopwatch.GetTimestamp();
            long beforeResume = 0;
            var originalBeforeStart = request.BeforeStart;
            var result = await inner.RunAsync(request with { BeforeStart = () => {
                originalBeforeStart?.Invoke(); beforeResume = Stopwatch.GetTimestamp();
            } }, token);
            var afterCall = Stopwatch.GetTimestamp();
            var phases = new List<Phase>();
            var parseErrors = 0;
            foreach (var line in result.StandardError.Split('\n'))
            {
                const string prefix = "P4_PARSER_PHASE ";
                if (!line.StartsWith(prefix, StringComparison.Ordinal)) continue;
                try
                {
                    using var json = JsonDocument.Parse(line[prefix.Length..], new JsonDocumentOptions { MaxDepth = 4 });
                    var item = json.RootElement;
                    var process = item.GetProperty("process").GetString();
                    var pid = item.GetProperty("processIdHash").GetString();
                    var stage = item.GetProperty("stage").GetString();
                    var ticks = item.GetProperty("ticks").GetInt64();
                    var previous = item.GetProperty("previousWriteTicks").GetInt64();
                    var frequency = item.GetProperty("frequency").GetInt64();
                    if (process is not { Length: 16 } || !process.All(Uri.IsHexDigit)
                        || pid is not { Length: 16 } || !pid.All(Uri.IsHexDigit)
                        || stage is null || !KnownPhases.Contains(stage) || ticks < beforeCall || ticks > afterCall
                        || previous < 0 || frequency != Stopwatch.Frequency)
                    { parseErrors++; continue; }
                    phases.Add(new(process, pid, stage, ticks, previous, frequency));
                }
                catch (Exception error) when (error is JsonException or InvalidOperationException
                    or KeyNotFoundException or FormatException or OverflowException)
                { parseErrors++; }
            }
            var prepared = Stopwatch.GetTimestamp();
            Console.WriteLine("PARSER_STAGE_METADATA_ONLY " + JsonSerializer.Serialize(new {
                atUtc = DateTimeOffset.UtcNow, scenario, sequence = call,
                beforeCallTicks = beforeCall, beforeResumeTicks = beforeResume == 0 ? (long?)null : beforeResume,
                afterCallTicks = afterCall, frequency = Stopwatch.Frequency,
                timeoutMilliseconds = request.Timeout.TotalMilliseconds,
                result.Started, exit = result.Exit.ToString(), result.ErrorCode, result.Truncated,
                stdoutUtf8Bytes = Encoding.UTF8.GetByteCount(result.StandardOutput),
                stderrUtf8Bytes = Encoding.UTF8.GetByteCount(result.StandardError),
                metadataPreparationMs = (prepared - afterCall) * 1000d / Stopwatch.Frequency,
                parseErrors, phases,
            }));
            return result;
        }
    }
}
