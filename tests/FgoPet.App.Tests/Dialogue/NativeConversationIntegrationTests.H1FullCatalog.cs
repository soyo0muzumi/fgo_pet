using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FgoPet.App.Bootstrap;
using FgoPet.Core.Dialogue;
using FgoPet.Core.Settings;
using FgoPet.Dialogue.Settings;
using FgoPet.Extensibility;
using FgoPet.Infrastructure.Persistence;
using FgoPet.Infrastructure.Providers;
using FgoPet.Kernel.Agent;
using FgoPet.Platform.Processes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;

namespace FgoPet.App.Tests.Dialogue;

// Only this fixture mutates the process-local storage-root environment variable.
[CollectionDefinition("H1 full catalog isolation", DisableParallelization = true)]
public sealed class H1FullCatalogCollection;

[Collection("H1 full catalog isolation")]
public sealed class H1FullCatalogMeasurementTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Full_production_catalog_is_measured_offline_with_preflight_and_dispatch_separated(bool subscription)
    {
        var prefix = subscription ? "buddy-" : "";
        var originalRoot = Environment.GetEnvironmentVariable("FGO_PET_STATE_ROOT");
        var root = Path.Combine(Path.GetTempPath(), "fgo-h1-full-catalog-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            Environment.SetEnvironmentVariable("FGO_PET_STATE_ROOT", root);
            var database = new RuntimeDatabase(Path.Combine(root, "runtime.db"), pooling: false);
            new RuntimeDatabaseMigrator(database).Migrate();
            var services = new ServiceCollection().AddFgoPet([]); // All production defaults included.
            services.Replace(ServiceDescriptor.Singleton(database));
            var credentials = new DeniedCredentials();
            services.Replace(ServiceDescriptor.Singleton<FgoPet.Core.Secrets.ICredentialReader>(credentials));
            services.Replace(ServiceDescriptor.Singleton<FgoPet.Infrastructure.Secrets.ICredentialReader>(credentials));
            var processes = new DeniedProcesses();
            services.Replace(ServiceDescriptor.Singleton<IBoundedProcessRunner>(processes));
            using var http = new DeniedHttp();
            services.Replace(ServiceDescriptor.Singleton(new HttpClient(http)));
            await using (var production = services.BuildServiceProvider())
            {
                Assert.Equal(root, production.GetRequiredService<AppPaths>().StorageRoot);
                // No production PluginRuntime.StartAsync, AppStartup, tool or observer is run.
                var catalog = production.GetRequiredService<PluginCatalog>();
                Assert.All(catalog.Plugins, plugin => Assert.False(
                    production.GetRequiredService<PluginRuntime>().IsActive(plugin.Manifest.Id)));
                Assert.Contains(catalog.Plugins, p => p.Manifest.Id == "firstparty.todo");
                Assert.Contains(catalog.Plugins, p => p.Manifest.Id == "firstparty.focus");
                Assert.Contains(catalog.Plugins, p => p.Manifest.Id == "firstparty.memory");
                Assert.Contains(catalog.Tools, t => t.Descriptor.Name == "understanding.query");
                var tools = catalog.Tools.Select(t => new CatalogTool(t.Descriptor)).ToArray();
                Assert.NotEmpty(tools);
                var inventory = new
                {
                    scope = "AddFgoPet defaults; registered descriptors, not granted/executable capabilities",
                    productionPluginsStarted = 0,
                    plugins = catalog.Plugins.Select(p => new { p.Manifest.Id, p.Manifest.Version,
                        tools = catalog.Tools.Count(t => t.PluginId == p.Manifest.Id) }).ToArray(),
                    tools = catalog.Tools.Select(t => new { t.PluginId, t.Descriptor.Name,
                        effect = t.Descriptor.Effect.ToString(), schemaHash = Digest(t.Descriptor.Parameters.GetRawText()),
                        schemaUtf8Bytes = Encoding.UTF8.GetByteCount(t.Descriptor.Parameters.GetRawText()),
                        descriptionHash = Digest(t.Descriptor.Description) }).ToArray(),
                };
                WriteEvidence(prefix + "production-catalog.json", inventory);
                foreach (var native in new[] { false, true })
                    await NativeConversationIntegrationTests.MeasureH1FullCatalogAsync(tools, native, subscription);
                Assert.All(tools, tool => Assert.Equal(0, tool.Invocations));
                Assert.Equal(0, credentials.Reads);
                Assert.Equal(0, processes.Attempts);
                Assert.Equal(0, http.Attempts);
                WriteEvidence(prefix + "isolation.json", new { productionPluginsStarted = 0, toolInvocations = 0,
                    credentialReads = credentials.Reads, boundedProcessAttempts = processes.Attempts,
                    injectedHttpAttempts = http.Attempts, realProviderCalls = 0,
                    storage = "fresh temporary root; process environment restored; no installed app state",
                    coverage = "DI HTTP guard is not a system network firewall; production tools are never invoked" });
            }
        }
        finally
        {
            Environment.SetEnvironmentVariable("FGO_PET_STATE_ROOT", originalRoot);
            // Preserve synthetic state as an explicit artifact; do not delete unrelated old H1 roots.
            WriteEvidence(prefix + "temporary-state.json", new { rootHash = Digest(root), retained = Directory.Exists(root),
                environmentRestored = Environment.GetEnvironmentVariable("FGO_PET_STATE_ROOT") == originalRoot,
                contents = Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
                    .Select(p => new { relativePath = Path.GetRelativePath(root, p), bytes = new FileInfo(p).Length }).ToArray() });
        }
    }

    internal static string Digest(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    internal static void WriteEvidence(string name, object metadata)
    {
        var root = Environment.GetEnvironmentVariable("FGO_PET_H1_CATALOG_EVIDENCE_ROOT");
        if (string.IsNullOrWhiteSpace(root)) return;
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, name), JsonSerializer.Serialize(metadata, new JsonSerializerOptions { WriteIndented = true }));
    }

    internal sealed class CatalogTool(ToolDescriptor descriptor) : IToolProvider
    {
        public ToolDescriptor Descriptor { get; } = descriptor;
        public int Invocations { get; private set; }
        public ValueTask<ToolResult> InvokeAsync(ToolInvocation invocation, CancellationToken cancellationToken)
        {
            Invocations++;
            throw new InvalidOperationException("H1_CATALOG_TOOL_EXECUTION_FORBIDDEN");
        }
    }
    private sealed class DeniedCredentials : FgoPet.Infrastructure.Secrets.ICredentialReader
    {
        public int Reads { get; private set; }
        public Task<string?> ReadAsync(string target, CancellationToken cancellationToken)
        { Reads++; throw new InvalidOperationException("H1_CREDENTIAL_READ_FORBIDDEN"); }
    }
    private sealed class DeniedProcesses : IBoundedProcessRunner
    {
        public int Attempts { get; private set; }
        public ValueTask<BoundedProcessResult> RunAsync(BoundedProcessRequest request, CancellationToken token)
        { Attempts++; throw new InvalidOperationException("H1_PROCESS_LAUNCH_FORBIDDEN"); }
    }
    private sealed class DeniedHttp : HttpMessageHandler
    {
        public int Attempts { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        { Attempts++; throw new InvalidOperationException("H1_HTTP_FORBIDDEN"); }
    }
}

public sealed partial class NativeConversationIntegrationTests
{
    internal static async Task MeasureH1FullCatalogAsync(IReadOnlyList<IToolProvider> tools, bool native, bool subscription = false)
    {
        var binding = ResolveSourcePersona();
        var settings = new TestSettingsStore(DialogueSettings.Defaults with
        {
            ModelConnection = subscription
                ? new("custom-openai-compatible", "http://127.0.0.1:8326/v1", "buddy/deepseek-v4.1-flash",
                    contextWindowOverride: 1_000_000, maxOutputTokens: 128_000)
                : new ModelConnectionSettings("fixture", "https://fixture.invalid/v1", "fixture-model",
                    contextWindowOverride: 16384, maxOutputTokens: 2048),
        });
        var route = ModelRouteKey.From(settings.Load().ModelConnection!);
        var meter = new FullCatalogMeter();
        ProviderTurn[] turns = native
            ? [PersonaFinal("Synthetic full catalog observation."), PersonaFinal("Synthetic full catalog reply.")]
            : [PersonaFinal("Synthetic full catalog reply.")];
        var provider = new ScriptedProvider(turns)
        { RequestReceived = request => meter.Dispatch(route, request) };
        var connection = settings.Load().ModelConnection!;
        IChatProvider boundProvider = subscription ? new CatalogRouteProvider(provider, connection) : provider;
        await using var host = await TestHost.CreateAsync(boundProvider, null, new RecordingObserver(),
            settings: settings, tokenMeter: meter, contentResolver: new BoundPersonaResolver(binding),
            useNative: native, skills: true, questions: true, extraTools: tools);
        var result = await host.Orchestrator.SendAsync(SourcePersonaRole, "合成验收：请简短回应。", default, new(ProjectId));
        var hostRunStatusCodes = await host.SafeRunErrorsAsync(new(result.ConversationId, SourcePersonaRole, ProjectId));
        var inputBudget = PromptBudget.Resolve(new ModelContextLimit(route, connection.ContextWindowOverride!.Value,
            connection.MaxOutputTokens, ContextLimitSource.Override, "declared host override; synthetic transport"),
            connection.MaxOutputTokens).InputTokens;
        Assert.NotEmpty(meter.Measurements);
        Assert.Equal(provider.Requests.Count, meter.Dispatches.Count);
        Assert.All(provider.Requests, request => Assert.Equal(connection.MaxOutputTokens, request.MaxOutputTokens));
        // All registered schemas are offered in the appropriate production projection; nothing is trimmed to pass.
        var expected = tools.Where(t => native || t.Descriptor.Effect != ToolEffect.Command)
            .Select(t => native ? ModelToolNameMap.GetWireName(t.Descriptor.Name) : t.Descriptor.Name).ToHashSet(StringComparer.Ordinal);
        if (native)
            expected.UnionWith(new[] { "skill.load", "user.ask", "response.finalize" }.Select(ModelToolNameMap.GetWireName));
        Assert.Contains(meter.Measurements, m => expected.SetEquals(m.ToolNames));
        Assert.All(meter.Dispatches, d => Assert.Contains(meter.Measurements,
            m => m.InputHash == d.InputHash && m.WireHash == d.WireHash && m.EstimatedInputTokens == d.EstimatedInputTokens));
        var metadata = new
        {
            scope = "complete production registered tool schemas; actual source Persona and existing host/skill/final instructions; synthetic empty history",
            exclusions = "production plugin lifecycle, context/prompt/observer contributions, grants, tool execution, review adapter and real transport",
            native, result = result.Status.ToString(), hostRunStatusCodes,
            syntheticProviderCalls = provider.Requests.Count, realProviderCalls = 0,
            inputLimit = subscription ? inputBudget : 16000, warningLimit = subscription ? inputBudget * 0.6 : 9600,
            outputLimit = connection.MaxOutputTokens, contextWindow = connection.ContextWindowOverride,
            configuredRoute = route, capacitySource = "declared host override; installed adapter is separately pinned by validation host; not provider usage",
            actualHostInputBudget = inputBudget,
            countKind = "Estimated; production RequestTokenMeter; no synthetic usage anchors",
            fullRegisteredCatalog = true, reviewAdapterVerified = false,
            measurements = meter.Measurements, dispatches = meter.Dispatches,
        };
        H1FullCatalogMeasurementTests.WriteEvidence((subscription ? "buddy-" : "") +
            (native ? "native-full-catalog.json" : "ordinary-full-catalog.json"), metadata);
        // The complete Native schema already exceeds this frozen context. Prove fail-closed admission,
        // while keeping its measurement distinct from an actual scripted or real provider dispatch.
        if (native && !subscription)
        {
            Assert.Equal(ConversationSendStatus.Failed, result.Status);
            Assert.Empty(provider.Requests);
            Assert.Contains("Failed:RUN_CONTEXT_TOO_LARGE", hostRunStatusCodes, StringComparison.Ordinal);
            Assert.Contains(meter.Measurements, m => m.ToolCount == expected.Count && m.EstimatedInputTokens > inputBudget);
        }
        else
        {
            Assert.Equal(ConversationSendStatus.Completed, result.Status);
            Assert.Equal(native ? 2 : 1, provider.Requests.Count);
            if (native)
            {
                Assert.NotEmpty(provider.Requests[0].Tools!);
                Assert.Null(provider.Requests[1].Tools);
            }
            Assert.All(meter.Dispatches, dispatch => Assert.InRange(dispatch.EstimatedInputTokens, 1, inputBudget));
        }
    }

    private sealed class CatalogRouteProvider(IChatProvider inner, ModelConnectionSettings connection) : IChatProvider
    {
        public string ProviderId => connection.ProviderId;
        public string ModelId => connection.ModelId;
        public IAsyncEnumerable<ChatStreamChunk> StreamAsync(ChatRequest request, CancellationToken token) => inner.StreamAsync(request, token);
        public Task<IReadOnlyList<ProviderModel>> ListModelsAsync(CancellationToken token) => inner.ListModelsAsync(token);
    }

    private sealed record CatalogRequestMetadata(int Sequence, DateTimeOffset AtUtc, string Stage,
        int MessageCount, int ToolCount, int? MaxOutputTokens, int InputBytes, int WireBytes,
        string InputHash, string WireHash, int EstimatedInputTokens, string CountKind, bool Warning9600,
        bool Over16000, IReadOnlyList<string> ToolNames, IReadOnlyList<int> MatchingPreflightSequences);

    private sealed class FullCatalogMeter : IRequestTokenMeter
    {
        private readonly RequestTokenMeter inner = new();
        public List<CatalogRequestMetadata> Measurements { get; } = [];
        public List<CatalogRequestMetadata> Dispatches { get; } = [];
        public TokenMeasurement Measure(ModelRouteKey route, ChatRequest request)
        {
            var measurement = inner.Measure(route, request);
            Measurements.Add(Snapshot(route, request, measurement, Measurements.Count + 1, "preflight-or-projection", []));
            return measurement;
        }
        public void Dispatch(ModelRouteKey route, ChatRequest request)
        {
            var snapshot = Snapshot(route, request, inner.Measure(route, request), Dispatches.Count + 1, "scripted-provider-dispatch", []);
            var matches = Measurements.Where(m => m.InputHash == snapshot.InputHash && m.WireHash == snapshot.WireHash)
                .Select(m => m.Sequence).ToArray();
            Assert.NotEmpty(matches);
            Dispatches.Add(snapshot with { MatchingPreflightSequences = matches });
        }
        private static CatalogRequestMetadata Snapshot(ModelRouteKey route, ChatRequest request,
            TokenMeasurement measurement, int sequence, string stage, IReadOnlyList<int> matches)
        {
            var input = ChatRequestInputEnvelope.Write(route.ModelId, request);
            var wire = ChatRequestPayloadWriter.Write(route, request);
            using var inputJson = JsonDocument.Parse(input);
            using var wireJson = JsonDocument.Parse(wire);
            Assert.Equal(inputJson.RootElement.GetProperty("messages").GetRawText(), wireJson.RootElement.GetProperty("messages").GetRawText());
            return new(sequence, DateTimeOffset.UtcNow, stage, request.EffectiveMessageCount, request.Tools?.Count ?? 0,
                request.MaxOutputTokens, Encoding.UTF8.GetByteCount(input), Encoding.UTF8.GetByteCount(wire),
                H1FullCatalogMeasurementTests.Digest(input), H1FullCatalogMeasurementTests.Digest(wire),
                measurement.InputTokens, measurement.Kind.ToString(), measurement.InputTokens > 9600, measurement.InputTokens > 16000,
                request.Tools?.Select(t => t.Name).ToArray() ?? [], matches);
        }
        public void RecordUsage(ModelRouteKey route, ChatRequest request, ChatUsage usage) { }
        public void Invalidate(ModelRouteKey route) => inner.Invalidate(route);
    }
}
