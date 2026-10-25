using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using FgoPet.App.Dialogue;
using FgoPet.Core.Dialogue;
using FgoPet.Kernel.Agent;
using FgoPet.Testing.Persona;
using Xunit;
using BatchFixture = FgoPet.App.Tests.Dialogue.P2BatchProviderTests.BatchFixture;

namespace FgoPet.App.Tests.Dialogue;

public sealed partial class NativeConversationIntegrationTests
{
    private const string MetadataCanary = "private-metadata-body-canary-中🍵";

    [Theory]
    [InlineData("broken")]
    [InlineData("unicode")]
    [InlineData("empty")]
    public void Metadata_only_capture_keeps_hashes_and_template_order_without_content(string scenario)
    {
        using var fixture = new BatchFixture(subscription: true);
        using var cleanup = new MetadataCaptureCleanup(fixture.DirectoryPath);
        var scope = P2Hash(Guid.NewGuid().ToString("N"));
        var raw = scenario == "empty" ? "" : MetadataCanary + (scenario == "unicode" ? "\ud800" : "{\n");
        using (var capture = new P2FailedCandidateCapture(fixture.DirectoryPath, metadataOnly: true))
        {
            Assert.True(capture.ObserveRequest(scope, 1, MetadataCaptureRequest(), StructuredOutputValidator.FinalStepInstructions));
            Assert.True(capture.TryCapture("synthetic", scope, 1, raw, "json-invalid", null));
            Assert.False(capture.TryCapture("synthetic", scope, 1, raw, "json-invalid", null));
        }
        var path = Path.Combine(fixture.DirectoryPath, P2FailedCandidateCapture.MetadataFileName);
        var safe = File.ReadAllText(path);
        using var record = JsonDocument.Parse(safe);
        Assert.Equal("metadata-only", record.RootElement.GetProperty("CaptureMode").GetString());
        Assert.Equal(P2ResponseDiagnostics.Hash(raw), record.RootElement.GetProperty("Body").GetProperty("Sha256").GetString());
        var template = Assert.Single(record.RootElement.GetProperty("Templates").EnumerateArray());
        Assert.Equal(4, template.GetProperty("MessageIndex").GetInt32());
        Assert.Equal(new[] { "Body", "Kind", "MessageIndex", "Role" }, template.EnumerateObject().Select(x => x.Name).Order(StringComparer.Ordinal));
        Assert.False(safe.Contains(MetadataCanary, StringComparison.Ordinal));
        Assert.False(safe.Contains("private-argument-key", StringComparison.Ordinal));
        Assert.False(safe.Contains(StructuredOutputValidator.FinalStepInstructions, StringComparison.Ordinal));
        Assert.DoesNotContain(new[] { "RawText", "RawUtf16LeBase64", "RawUtf16Sha256", "Text" },
            key => record.RootElement.TryGetProperty(key, out _));
        Assert.False(File.Exists(Path.Combine(fixture.DirectoryPath, P2FailedCandidateCapture.FileName)));
        WriteMetadataEvidence("capture-" + scenario, safe);
        File.Delete(path);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Metadata_only_capture_failure_does_not_overwrite_or_accept_more_candidates(bool oversized)
    {
        using var fixture = new BatchFixture(subscription: true);
        using var cleanup = new MetadataCaptureCleanup(fixture.DirectoryPath);
        var scope = P2Hash("synthetic-scope");
        using (var capture = new P2FailedCandidateCapture(fixture.DirectoryPath, metadataOnly: true))
        {
            Assert.True(capture.ObserveRequest(scope, 1, MetadataCaptureRequest(), StructuredOutputValidator.FinalStepInstructions));
            if (oversized)
            {
                Assert.False(capture.TryCapture("synthetic", scope, 1, new string('x', 8 * 1024 * 1024 + 1), "json-invalid", null));
                Assert.False(capture.ObserveRequest(scope, 2, MetadataCaptureRequest(), StructuredOutputValidator.FinalStepInstructions));
            }
            else
            {
                capture.Dispose();
                Assert.False(capture.TryCapture("synthetic", scope, 1, MetadataCanary, "json-invalid", null));
                Assert.False(capture.ObserveRequest(scope, 2, MetadataCaptureRequest(), StructuredOutputValidator.FinalStepInstructions));
            }
        }
        var path = Path.Combine(fixture.DirectoryPath, P2FailedCandidateCapture.MetadataFileName);
        var before = SHA256.HashData(File.ReadAllBytes(path));
        Assert.Throws<IOException>(() => new P2FailedCandidateCapture(fixture.DirectoryPath, metadataOnly: true));
        Assert.Equal(before, SHA256.HashData(File.ReadAllBytes(path)));
        Assert.Equal(0, new FileInfo(path).Length);
        File.Delete(path);
    }

    [Fact]
    public async Task Metadata_only_contract_keeps_one_format_repair_and_no_body_files()
    {
        using var fixture = new BatchFixture(subscription: true, maxRequests: 3);
        var malformed = "{\"text\":\"" + MetadataCanary;
        fixture.Respond = (_, _) => Task.FromResult(P2TextResponse(fixture.Sends == 3
            ? "{\"text\":\"" + MetadataCanary + "\",\"emotion\":\"neutral\"}" : malformed));
        var report = Path.Combine(fixture.DirectoryPath, "metadata-contract");
        _ = await RunContractScenes(fixture.Batch, report, ResolveSourcePersona(), false);
        Assert.Equal(3, fixture.Sends);
        Assert.False(File.Exists(Path.Combine(report, "synthetic-replies.txt")));
        Assert.False(File.Exists(Path.Combine(report, P2FailedCandidateCapture.FileName)));
        var candidates = File.ReadAllLines(Path.Combine(report, P2FailedCandidateCapture.MetadataFileName));
        Assert.Single(candidates);
        using var candidate = JsonDocument.Parse(candidates[0]);
        Assert.Equal(2, candidate.RootElement.GetProperty("Request").GetInt32());
        Assert.Equal(2, File.ReadAllLines(Path.Combine(report, "model-finals.jsonl")).Length);
        foreach (var file in Directory.GetFiles(report))
        {
            Assert.False(File.ReadAllText(file).Contains(MetadataCanary, StringComparison.Ordinal));
            File.Delete(file);
        }
        WriteMetadataEvidence("contract-format-repair", candidates[0]);
        Directory.Delete(report);
    }

    private static ChatRequest MetadataCaptureRequest() => ChatRequest.CreateAgent("fixture", "conversation",
        [new(ModelMessageRole.System, MetadataCanary, []),
         new(ModelMessageRole.User, MetadataCanary, []),
         new(ModelMessageRole.Assistant, "", [new("fixture-call", "fixture.read", "{\"private-argument-key\":\"" + MetadataCanary + "\"}")]),
         new(ModelMessageRole.Tool, MetadataCanary, [], "fixture-call"),
         new(ModelMessageRole.System, StructuredOutputValidator.FinalStepInstructions, [])], maxOutputTokens: 2048);

    private sealed class MetadataCaptureCleanup(string directory) : IDisposable
    {
        public void Dispose() => File.Delete(Path.Combine(directory, P2FailedCandidateCapture.MetadataFileName));
    }

    private static void WriteMetadataEvidence(string scenario, string safe)
    {
        var directory = Environment.GetEnvironmentVariable("FGO_PET_P2_BUDGET_EVIDENCE");
        if (string.IsNullOrWhiteSpace(directory)) return;
        Directory.CreateDirectory(directory);
        using var record = JsonDocument.Parse(safe);
        File.AppendAllText(Path.Combine(directory, "metadata-only-candidates.jsonl"),
            JsonSerializer.Serialize(new { Scenario = scenario, RealModelCalls = 0, Metadata = record.RootElement }) + "\n");
    }
}
