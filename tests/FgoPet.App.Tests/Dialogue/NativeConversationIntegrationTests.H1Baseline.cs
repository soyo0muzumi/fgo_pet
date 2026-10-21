using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FgoPet.Core.Dialogue;
using FgoPet.Core.Settings;
using FgoPet.Extensibility;
using FgoPet.Dialogue.Settings;
using FgoPet.Infrastructure.Providers;
using Xunit;

namespace FgoPet.App.Tests.Dialogue;

public sealed partial class NativeConversationIntegrationTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task H1_baseline_captures_actual_persona_envelopes_with_metadata_only_and_no_network(bool native)
    {
        var binding = ResolveSourcePersona();
        var provider = new ScriptedProvider(PersonaFinal("Synthetic baseline reply."));
        var settings = new TestSettingsStore(DialogueSettings.Defaults with
        {
            ModelConnection = new ModelConnectionSettings("fixture", "https://fixture.invalid/v1", "fixture-model",
                contextWindowOverride: 16384, maxOutputTokens: 2048),
        });
        var meter = new H1MetadataOnlyMeter();
        await using var host = await TestHost.CreateAsync(provider, new RecordingTool(ToolEffect.ReadOnly), new RecordingObserver(),
            settings: settings, tokenMeter: meter, contentResolver: new BoundPersonaResolver(binding), useNative: native, skills: true, questions: true);
        var result = await host.Orchestrator.SendAsync(SourcePersonaRole, "合成验收：请简短回应。", default, new(ProjectId));
        Assert.NotEmpty(meter.Records);
        Assert.All(provider.Requests, request => Assert.Equal(2048, request.MaxOutputTokens));
        var metadata = JsonSerializer.Serialize(new
        {
            scope = "actual-source-persona; scripted-provider; skills/question/one-fixture-tool subset",
            native, result = result.Status.ToString(), syntheticProviderCalls = provider.Requests.Count, realProviderCalls = 0,
            inputLimit = 16000, warningLimit = 9600, outputLimit = 2048,
            fullProductionCatalogVerified = false, reviewAdapterVerified = false, measurements = meter.Records,
        });
        var outputRoot = Environment.GetEnvironmentVariable("FGO_PET_H1_EVIDENCE_ROOT");
        if (!string.IsNullOrWhiteSpace(outputRoot))
        {
            Directory.CreateDirectory(outputRoot);
            await File.WriteAllTextAsync(Path.Combine(outputRoot, native ? "native-request-baseline.json" : "ordinary-request-baseline.json"), metadata);
        }
        Console.WriteLine("H1_METADATA_ONLY " + metadata);
    }

    private sealed class H1MetadataOnlyMeter : IRequestTokenMeter
    {
        private readonly RequestTokenMeter _inner = new();
        public List<object> Records { get; } = [];
        public TokenMeasurement Measure(ModelRouteKey route, ChatRequest request)
        {
            var measurement = _inner.Measure(route, request);
            var input = ChatRequestInputEnvelope.Write(route.ModelId, request);
            var wire = ChatRequestPayloadWriter.Write(route, request);
            using var inputJson = JsonDocument.Parse(input); using var wireJson = JsonDocument.Parse(wire);
            Assert.Equal(inputJson.RootElement.GetProperty("messages").GetRawText(), wireJson.RootElement.GetProperty("messages").GetRawText());
            Records.Add(new
            {
                atUtc = DateTimeOffset.UtcNow, request.IsAgentRequest, messageCount = request.EffectiveMessageCount,
                toolCount = request.Tools?.Count ?? 0, request.MaxOutputTokens,
                inputBytes = Encoding.UTF8.GetByteCount(input), wireBytes = Encoding.UTF8.GetByteCount(wire),
                inputHash = Hash(input), wireHash = Hash(wire), estimatedInputTokens = measurement.InputTokens,
                countKind = measurement.Kind.ToString(), warning9600 = measurement.InputTokens > 9600,
                over16000 = measurement.InputTokens > 16000,
            });
            return measurement;
        }
        // Scripted usage is not evidence of real tokenization and must never become a usage anchor.
        public void RecordUsage(ModelRouteKey route, ChatRequest request, ChatUsage usage) { }
        public void Invalidate(ModelRouteKey route) => _inner.Invalidate(route);
        private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    }
}
