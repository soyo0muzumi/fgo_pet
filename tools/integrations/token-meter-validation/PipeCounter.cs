using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FgoPet.Infrastructure.Providers;

namespace FgoPet.Testing.Tokenization;

internal sealed class PipeCounter : ILocalReferenceTokenCounter, IDisposable
{
    private const string PinnedWorkerHash = "45DFD921D7DAA8881D14CAA1C63165E17F854D217240A9056DAFBD92818F4C26";
    private const string PinnedPolicyHash = "E730555C1F1D9BD901CB96F011F6A13BF3DDA55967B6CB2C159AB74699365414";
    private readonly Process process;
    private readonly Task<string> stderr;
    private readonly object gate = new();
    private bool disposed;
    public JsonElement Startup { get; }
    public bool Ready => Startup.TryGetProperty("ready", out var ready) && ready.ValueKind == JsonValueKind.True
        && MatchesStartupHash("workerHash", PinnedWorkerHash) && MatchesStartupHash("policyHash", PinnedPolicyHash);
    public string PolicyHash => Ready ? PinnedPolicyHash : string.Empty;
    public List<JsonElement> Metadata { get; } = [];
    public PipeCounter(string python, string worker, string assets)
    {
        if (new FileInfo(worker).Length > 64 * 1024
            || Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(worker))) != PinnedWorkerHash)
            throw new InvalidOperationException("LOCAL_REFERENCE_WORKER_UNVERIFIED");
        var started = Stopwatch.GetTimestamp();
        var start = new ProcessStartInfo(python) { UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardInputEncoding = new UTF8Encoding(false), StandardOutputEncoding = new UTF8Encoding(false) };
        foreach (var argument in new[] { "-I", "-B", "-X", "utf8", worker, assets }) start.ArgumentList.Add(argument);
        process = Process.Start(start) ?? throw new IOException("LOCAL_REFERENCE_PROCESS_START"); stderr = process.StandardError.ReadToEndAsync();
        try
        {
            var line = process.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(30)).GetAwaiter().GetResult();
            using var json = JsonDocument.Parse(line ?? "{}");
            var fields = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json.RootElement.GetRawText())!;
            fields["totalColdStartMs"] = JsonSerializer.SerializeToElement(Stopwatch.GetElapsedTime(started).TotalMilliseconds);
            Startup = JsonSerializer.SerializeToElement(fields);
            if (!Ready) Dispose();
        }
        catch { Dispose(); throw; }
    }
    private bool MatchesStartupHash(string property, string expected) => Startup.TryGetProperty(property, out var value)
        && value.ValueKind == JsonValueKind.String && StringComparer.Ordinal.Equals(value.GetString(), expected);
    public LocalReferenceTokenCount? TryCount(string input, string wire)
    {
        lock (gate) return TryCountLocked(input, wire);
    }
    private LocalReferenceTokenCount? TryCountLocked(string input, string wire)
    {
        if (disposed) return null;
        try
        {
            var request = JsonSerializer.Serialize(new { input, wire }, new JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
            if (Encoding.UTF8.GetByteCount(request) > 1024 * 1024) return null;
            process.StandardInput.WriteLine(request); process.StandardInput.Flush();
            var line = process.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
            if (line is null || line.Length > 64 * 1024) { Dispose(); return null; }
            using var json = JsonDocument.Parse(line); var item = json.RootElement.Clone(); Metadata.Add(item);
            if (!item.TryGetProperty("verified", out var verified) || !verified.GetBoolean()) return null;
            return new(item.GetProperty("inputTokens").GetInt32(), item.GetProperty("inputHash").GetString()!, item.GetProperty("wireHash").GetString()!, item.GetProperty("policyHash").GetString()!);
        }
        catch (Exception error) when (error is IOException or TimeoutException or JsonException or InvalidOperationException or KeyNotFoundException or FormatException or OverflowException)
        { Dispose(); return null; }
    }
    public void Dispose()
    {
        lock (gate)
        {
            if (disposed) return; disposed = true;
            try { process.StandardInput.Close(); if (!process.WaitForExit(5000)) { process.Kill(entireProcessTree: true); process.WaitForExit(); } }
            finally { _ = stderr.GetAwaiter().GetResult(); process.Dispose(); }
        }
    }
}
