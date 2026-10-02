using System.IO;
using System.Text.Json;

namespace FgoPet.RenderingProbe.Diagnostics;

public sealed class ProbeRecorder
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web) { WriteIndented = false };
    private readonly string _outputDirectory;

    public ProbeRecorder(string outputDirectory)
    {
        _outputDirectory = Path.GetFullPath(outputDirectory);
        Directory.CreateDirectory(_outputDirectory);
    }

    public void Append(ProbeSample sample)
    {
        var line = JsonSerializer.Serialize(sample, Options) + Environment.NewLine;
        File.AppendAllText(Path.Combine(_outputDirectory, "samples.jsonl"), line);
    }

    public void WriteSummary(SessionSummary summary)
    {
        File.WriteAllText(
            Path.Combine(_outputDirectory, "session-summary.json"),
            JsonSerializer.Serialize(summary, new JsonSerializerOptions(Options) { WriteIndented = true }));
    }
}
