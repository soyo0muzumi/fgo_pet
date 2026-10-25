"""Generate a non-production timing variant of the current trusted PDF helper."""
from pathlib import Path
import hashlib
import json
import sys

root = Path(__file__).resolve().parents[3]
assert len(sys.argv) == 3, "PARSER_PROBE_ARGUMENTS"
source, output = (Path(value).resolve() for value in sys.argv[1:])
assert source == root / "plugins/FgoPet.Plugin.Documents/Parser/Program.cs", "PARSER_PROBE_SOURCE"
assert output.is_relative_to(root) and output.name == "ParserProgram.g.cs", "PARSER_PROBE_OUTPUT"
raw = source.read_bytes()
original = raw.decode("utf-8").replace("\r\n", "\n")
assert "ProbePhaseLog" not in original, "PARSER_PROBE_SOURCE_CHANGED"
operations = [
    ('Console.OutputEncoding = new UTF8Encoding(false);', "before", "managed_entry"),
    ('    using var input = Console.OpenStandardInput();', "before", "stdin_begin"),
    ('    var bytes = retained.ToArray();', "before", "stdin_eof"),
    ('    using var document = PdfDocument.Open(bytes, new ParsingOptions { UseLenientParsing = false });', "before", "pdf_open_begin"),
    ('    using var document = PdfDocument.Open(bytes, new ParsingOptions { UseLenientParsing = false });', "after", "pdf_open_end"),
    ('    var text = ContentOrderTextExtractor.GetText(document.GetPage(page));', "before", "extract_begin"),
    ('    var text = ContentOrderTextExtractor.GetText(document.GetPage(page));', "after", "extract_end"),
    ('    Console.Write(result);', "before", "result_ready"),
    ('    Console.Write(result);', "after", "stdout_emitted"),
    ('    Console.Write(JsonSerializer.Serialize(new { success = false, errorCode = code }, json));', "before", "failure_ready"),
    ('    Console.Write(JsonSerializer.Serialize(new { success = false, errorCode = code }, json));', "after", "failure_emitted"),
]
instrumented = original
for anchor, placement, phase in operations:
    assert instrumented.count(anchor) == 1, "PARSER_PROBE_ANCHOR_CHANGED"
    indent = anchor[:len(anchor) - len(anchor.lstrip())]
    call = indent + 'ProbePhaseLog.Write("' + phase + '", System.Diagnostics.Stopwatch.GetTimestamp());'
    replacement = call + "\n" + anchor if placement == "before" else anchor + "\n" + call
    instrumented = instrumented.replace(anchor, replacement, 1)
restored = "".join(line for line in instrumented.splitlines(keepends=True)
                   if not line.lstrip().startswith("ProbePhaseLog.Write("))
assert restored == original, "PARSER_PROBE_BUSINESS_SOURCE_CHANGED"

sink = r'''
// Diagnostic variant only. Original parser code and all its bounds remain above.
static class ProbePhaseLog
{
    private static string? processKey;
    private static string? processIdHash;
    private static long previousWriteTicks;
    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct FileTime { public uint Low, High; }
    [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetProcessTimes(IntPtr process, out FileTime created, out FileTime exited,
        out FileTime kernel, out FileTime user);
    public static void Write(string stage, long atTicks)
    {
        try
        {
            if (processKey is null)
            {
                var saltText = Environment.GetEnvironmentVariable("FGO_PET_PARSER_PHASE_SALT");
                if (saltText is null) return;
                var salt = Convert.FromHexString(saltText);
                if (salt.Length != 16 || !GetProcessTimes(new IntPtr(-1), out var created, out _, out _, out _)) return;
                var pid = Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture);
                var birth = ((ulong)created.High << 32 | created.Low).ToString(System.Globalization.CultureInfo.InvariantCulture);
                string Correlate(string value)
                {
                    var bytes = new byte[salt.Length + value.Length];
                    salt.CopyTo(bytes, 0);
                    for (var index = 0; index < value.Length; index++) bytes[salt.Length + index] = (byte)value[index];
                    return Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant()[..16];
                }
                processKey = Correlate(pid + ":" + birth);
                processIdHash = Correlate(pid + ":0");
            }
            var culture = System.Globalization.CultureInfo.InvariantCulture;
            var line = "{\"process\":\"" + processKey + "\",\"processIdHash\":\"" + processIdHash
                + "\",\"stage\":\"" + stage + "\",\"atUtc\":\"" + DateTimeOffset.UtcNow.ToString("O", culture)
                + "\",\"ticks\":" + atTicks.ToString(culture)
                + ",\"previousWriteTicks\":" + previousWriteTicks.ToString(culture)
                + ",\"frequency\":" + System.Diagnostics.Stopwatch.Frequency.ToString(culture) + "}\n";
            Console.Error.Write("P4_PARSER_PHASE " + line);
            previousWriteTicks = System.Diagnostics.Stopwatch.GetTimestamp() - atTicks;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or FormatException or ArgumentException)
        {
            // A diagnostic sink failure must not change the parser's business result.
            Console.Error.WriteLine("P4_PARSER_PHASE_SINK_UNVERIFIED");
        }
    }
}
'''
generated = ("#nullable enable\n" + instrumented + sink).encode("utf-8")
output.parent.mkdir(parents=True, exist_ok=True)
output.write_bytes(generated)
manifest = {
    "productionSourceSha256": hashlib.sha256(raw).hexdigest(),
    "generatorSha256": hashlib.sha256(Path(__file__).read_bytes()).hexdigest(),
    "generatedSourceSha256": hashlib.sha256(generated).hexdigest(),
    "businessSourceRestoredExactlyAfterRemovingMarkers": True,
    "addedPhases": [phase for _, _, phase in operations],
    "nonProduction": True, "realProviderCalls": 0,
}
output.with_suffix(".manifest.json").write_text(json.dumps(manifest, indent=2) + "\n", encoding="utf-8")
print(json.dumps(manifest))
