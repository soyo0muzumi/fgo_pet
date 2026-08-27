using System.Text.Json;
using FgoPet.RenderingProbe.Diagnostics;

namespace FgoPet.RenderingProbe.Tests;

public sealed class ProbeRecorderTests
{
    [Fact]
    public void Append_writes_camel_case_jsonl_and_releases_file()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"fgo-recorder-{Guid.NewGuid():N}");
        try
        {
            var recorder = new ProbeRecorder(directory);
            recorder.Append(new ProbeSample(DateTimeOffset.UnixEpoch, "wpf", "conventional", "r01c01", 0.6, 1.25, 12.5, 42_000_000));
            var path = Path.Combine(directory, "samples.jsonl");
            using (File.Open(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) { }
            var line = File.ReadAllText(path);
            using var document = JsonDocument.Parse(line);

            Assert.True(document.RootElement.TryGetProperty("workingSetBytes", out _));
            Assert.DoesNotContain("apiKey", line, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("prompt", line, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("chatText", line, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    }
}
