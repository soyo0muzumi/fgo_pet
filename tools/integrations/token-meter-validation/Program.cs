using FgoPet.Testing.Tokenization;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FgoPet.App.Bootstrap;
using FgoPet.App.Dialogue;
using FgoPet.App.Tests.Dialogue;
using FgoPet.Core.Dialogue;
using FgoPet.Core.Settings;
using FgoPet.Dialogue.Settings;
using FgoPet.Extensibility;
using FgoPet.Infrastructure.Persistence;
using FgoPet.Infrastructure.Providers;
using FgoPet.Kernel.Agent;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

internal static class Program
{
    internal static string Hash(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
    internal static void Check(bool value, string code) { if (!value) throw new ValidationFailure(code); }
    internal static void Save(string output, string name, object value) => File.WriteAllText(Path.Combine(output, name), JsonSerializer.Serialize(value, new JsonSerializerOptions { WriteIndented = true }));
    public static async Task<int> Main(string[] args)
    {
        try
        {
            Check(args.Length == 4 || args.Length == 5 && args[4] == "--expect-unavailable", "LOCAL_REFERENCE_ARGUMENTS");
            var output = Path.GetFullPath(args[3]); Check(!Directory.Exists(output), "LOCAL_REFERENCE_OUTPUT_EXISTS"); Directory.CreateDirectory(output);
            using var counter = new PipeCounter(args[0], Path.GetFullPath(args[1]), Path.GetFullPath(args[2]));
            Save(output, "counter-startup.json", counter.Startup);
            Check(counter.Ready == (args.Length == 4), "LOCAL_REFERENCE_UNEXPECTED_AVAILABILITY");
            var previous = Environment.GetEnvironmentVariable("FGO_PET_STATE_ROOT");
            var state = Path.Combine(Path.GetTempPath(), "fgo-p4-ref", Guid.NewGuid().ToString("N")); Directory.CreateDirectory(state);
            Environment.SetEnvironmentVariable("FGO_PET_STATE_ROOT", state);
            try
            {
                var database = new RuntimeDatabase(Path.Combine(state, "runtime.db"), pooling: false); new RuntimeDatabaseMigrator(database).Migrate();
                var services = new ServiceCollection().AddFgoPet([]); services.Replace(ServiceDescriptor.Singleton(database));
                var credentials = new DeniedCredentials(); using var httpGuard = new DeniedHttp(); using var http = new HttpClient(httpGuard);
                services.Replace(ServiceDescriptor.Singleton<FgoPet.Infrastructure.Secrets.ICredentialReader>(credentials));
                services.Replace(ServiceDescriptor.Singleton<FgoPet.Core.Secrets.ICredentialReader>(credentials)); services.Replace(ServiceDescriptor.Singleton(http));
                await using var production = services.BuildServiceProvider();
                var catalog = production.GetRequiredService<PluginCatalog>();
                Check(catalog.Plugins.All(p => !production.GetRequiredService<PluginRuntime>().IsActive(p.Manifest.Id)), "LOCAL_REFERENCE_PLUGIN_ACTIVATED");
                var descriptors = catalog.Tools.Select(t => t.Descriptor).ToArray();
                await Profile(descriptors, counter, output, "ordinary", native: false);
                await Profile(descriptors, counter, output, "native", native: true);
                await Profile(descriptors, counter, output, "ordinary-read", native: false, read: true);
                await Profile(descriptors, counter, output, "native-over-budget", native: true, oversized: true);
                Check(credentials.Attempts == 0 && httpGuard.Attempts == 0, "LOCAL_REFERENCE_EXTERNAL_ATTEMPT");
                Save(output, "isolation.json", new { realProviderCalls = 0, credentialReads = credentials.Attempts, httpRequests = httpGuard.Attempts,
                    productionPluginsStarted = 0, productionCompositionChanged = false, stateRootHash = Hash(state), inputLimit = 16000, outputLimit = 2048,
                    referenceAvailable = counter.Ready, semanticAcceptance = false, H1Accepted = false, P4Accepted = false });
            }
            finally { Environment.SetEnvironmentVariable("FGO_PET_STATE_ROOT", previous); }
            if (counter.Ready)
            {
                var route = ModelRouteKey.From(new ModelConnectionSettings("deepseek", "https://api.deepseek.com/v1", "deepseek-flash", maxOutputTokens: 2048));
                var concurrent = await Task.WhenAll(Enumerable.Range(0, 4).Select(index => Task.Run(() =>
                {
                    var isolatedMeter = new LocalReferenceRequestTokenMeter(route, counter.PolicyHash, counter);
                    var input = new ChatRequest("fixture", "parallel", [new(ChatMessageRole.User, "Synthetic parallel " + index)], maxOutputTokens: 2048);
                    return isolatedMeter.Measure(route, input).Kind == TokenCountKind.LocalReferenceEstimate;
                })));
                Check(concurrent.All(value => value), "LOCAL_REFERENCE_PIPE_INTERLEAVING");
                Save(output, "concurrent-counter.json", new { independentMeters = 4, allVerified = true, realProviderCalls = 0 });
                var meter = new LocalReferenceRequestTokenMeter(route, counter.PolicyHash, counter);
                var request = new ChatRequest("fixture", "synthetic", [new(ChatMessageRole.User, new string('中', 6000))], maxOutputTokens: 2048);
                var before = meter.Measure(route, request); Check(before.Kind == TokenCountKind.LocalReferenceEstimate, "LOCAL_REFERENCE_LOSS_PRECONDITION");
                counter.Dispose(); meter.Invalidate(route); var after = meter.Measure(route, request);
                Check(after.Kind == TokenCountKind.Estimated && after.InputTokens > 16000, "LOCAL_REFERENCE_LOSS_NOT_CONSERVATIVE");
                Save(output, "counter-loss.json", new { before.InputTokens, before.Kind, fallbackTokens = after.InputTokens,
                    fallbackKind = after.Kind.ToString(), newProcessStarted = false, realProviderCalls = 0 });
            }
            Save(output, "counter-requests.json", counter.Metadata);
            Console.WriteLine(JsonSerializer.Serialize(new { offlineValidated = true, realProviderCalls = 0, H1Accepted = false, P4Accepted = false }));
            return 0;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine(error is ValidationFailure failure ? failure.Message
                : error is InvalidOperationException { Message: "LOCAL_REFERENCE_WORKER_UNVERIFIED" } ? "LOCAL_REFERENCE_WORKER_UNVERIFIED"
                : "LOCAL_REFERENCE_VALIDATION_" + error.GetType().Name);
            return 1;
        }
    }

    private const BindingFlags Hidden = BindingFlags.Public | BindingFlags.NonPublic;
    private static object New(Type type, params object?[] values) => Activator.CreateInstance(type, Hidden | BindingFlags.Instance, null, values, null)!;
    private static async Task Profile(ToolDescriptor[] descriptors, PipeCounter counter, string output, string label, bool native, bool read = false, bool oversized = false)
    {
        var type = typeof(NativeConversationIntegrationTests); Type Nested(string name) => type.GetNestedType(name, Hidden)!;
        var connection = new ModelConnectionSettings("deepseek", "https://api.deepseek.com/v1", "deepseek-flash", maxOutputTokens: 2048);
        var route = ModelRouteKey.From(connection); var meter = new MeterProbe(route, counter);
        var tools = descriptors.Select(d => new FixtureTool(d, read && d.Name == "experience.read")).ToArray();
        var turns = new List<object>();
        if (read)
        {
            var args = JsonSerializer.Serialize(new { sourceId = "synthetic-source", sourceVersion = new string('a', 64), maxChars = 2000 });
            var chunks = new List<ChatStreamChunk> {
                new("", ReasoningDelta: "SYNTHETIC_TRANSIENT_REASONING_FOR_TOKEN_ACCOUNTING"),
                new("", ToolCallDelta: new(0, "synthetic-read", ModelToolNameMap.GetWireName("experience.read"), args)),
                new("", IsComplete: true, FinishReason: "tool_calls")
            };
            turns.Add(New(Nested("ProviderTurn"), chunks, null, null));
        }
        turns.Add(type.GetMethod("PersonaFinal", Hidden | BindingFlags.Static)!.Invoke(null, ["Synthetic bounded reply."])!);
        var array = Array.CreateInstance(Nested("ProviderTurn"), turns.Count); for (var i = 0; i < turns.Count; i++) array.SetValue(turns[i], i);
        var provider = New(Nested("ScriptedProvider"), [array]);
        provider.GetType().GetProperty("RequestReceived")!.SetValue(provider, new Action<ChatRequest>(meter.Dispatch));
        var bounded = New(Nested("LimitedProvider"), provider, 16000);
        var binding = type.GetMethod("ResolveSourcePersona", Hidden | BindingFlags.Static)!.Invoke(null, null)!;
        var create = Nested("TestHost").GetMethod("CreateAsync", Hidden | BindingFlags.Static)!;
        var replacements = new Dictionary<string, object?> {
            ["provider"] = bounded, ["tool"] = null, ["observer"] = New(Nested("RecordingObserver")),
            ["settings"] = New(Nested("TestSettingsStore"), DialogueSettings.Defaults with { ModelConnection = connection }),
            ["tokenMeter"] = meter, ["contentResolver"] = New(Nested("BoundPersonaResolver"), binding),
            ["useNative"] = native, ["skills"] = true, ["questions"] = true, ["extraTools"] = tools.Cast<IToolProvider>().ToArray()
        };
        var task = (Task)create.Invoke(null, create.GetParameters().Select(p => replacements.TryGetValue(p.Name!, out var value) ? value : p.DefaultValue).ToArray())!;
        await task; var host = task.GetType().GetProperty("Result")!.GetValue(task)!;
        try
        {
            var orchestrator = (ConversationOrchestrator)host.GetType().GetProperty("Orchestrator")!.GetValue(host)!;
            var role = (string)type.GetField("SourcePersonaRole", Hidden | BindingFlags.Static)!.GetRawConstantValue()!;
            var project = (string)type.GetField("ProjectId", Hidden | BindingFlags.Static)!.GetRawConstantValue()!;
            var result = await orchestrator.SendAsync(role, oversized ? new string('中', 10000) : "合成验收：请简短回应。", default, new(project));
            var expected = descriptors.Where(t => native || t.Effect != ToolEffect.Command).Select(t => native ? ModelToolNameMap.GetWireName(t.Name) : t.Name).ToHashSet(StringComparer.Ordinal);
            if (native) expected.UnionWith(new[] { "skill.load", "user.ask", "response.finalize" }.Select(ModelToolNameMap.GetWireName));
            Check(expected.Count == (native ? 31 : 18), "LOCAL_REFERENCE_CATALOG_COUNT");
            Check(meter.Offered.Any(names => expected.SetEquals(names)), "LOCAL_REFERENCE_CATALOG_PRUNED");
            Save(output, label + ".json", new { label, native, result = result.Status.ToString(), scriptedCalls = meter.Dispatches,
                realProviderCalls = 0, expectedTools = expected.Count, fullCatalogPresent = true, sourcePersonaUnchanged = true,
                toolInvocations = tools.Sum(t => t.Attempts), measurements = meter.Rows, inputLimit = 16000, outputLimit = 2048 });
            if (oversized || !counter.Ready && (native || read)) Check(result.Status == ConversationSendStatus.Failed
                && meter.Dispatches == (!counter.Ready && read ? 1 : 0) && meter.Rows.Any(r => r.InputTokens > 16000), "LOCAL_REFERENCE_BUDGET_NOT_ENFORCED");
            else Check(result.Status == ConversationSendStatus.Completed && meter.Dispatches > 0, "LOCAL_REFERENCE_REPLY_NOT_COMPLETED");
            Check(tools.Sum(t => t.Attempts) == (read ? 1 : 0), "LOCAL_REFERENCE_BUSINESS_EFFECT");
            if (counter.Ready && !oversized)
            {
                var captured = meter.LastFullRequest ?? throw new ValidationFailure("LOCAL_REFERENCE_NO_FULL_DISPATCH");
                var gate = new InputGate(meter); using var handler = new SyntheticHttp(); using var client = new HttpClient(handler);
                var adapter = new OpenAiCompatibleChatProvider("deepseek", new Uri("https://api.deepseek.com/v1"), "deepseek-flash",
                    new SyntheticCredential(), client, gate, route);
                await foreach (var chunk in adapter.StreamAsync(captured, default)) { }
                Check(handler.Calls == 1 && gate.Admissions == 1 && handler.WireHash == Hash(ChatRequestPayloadWriter.Write(route, captured)), "LOCAL_REFERENCE_HTTP_HANDOFF_MISMATCH");
                Save(output, label + "-http.json", new { syntheticHttpCalls = handler.Calls, gateAdmissions = gate.Admissions,
                    wireHash = handler.WireHash, fullTools = captured.Tools?.Count, realProviderCalls = 0, realCredentialReads = 0 });
            }
        }
        finally { await ((IAsyncDisposable)host).DisposeAsync(); }
    }

    private sealed class FixtureTool(ToolDescriptor descriptor, bool allowRead) : IToolProvider
    {
        public ToolDescriptor Descriptor => descriptor;
        public int Attempts { get; private set; }
        public ValueTask<ToolResult> InvokeAsync(ToolInvocation invocation, CancellationToken token)
        {
            token.ThrowIfCancellationRequested(); Attempts++; Check(allowRead && descriptor.Effect == ToolEffect.ReadOnly, "LOCAL_REFERENCE_TOOL_FORBIDDEN");
            return ValueTask.FromResult(new ToolResult(true, JsonSerializer.SerializeToElement(new { text = new string('中', 1000), sourceId = "synthetic-source", coverage = "synthetic_page" })));
        }
    }
    internal sealed record MeasurementRow(int InputTokens, int ConservativeInputTokens, string Kind, int Tools, string InputHash, string WireHash,
        string? ReferencePolicyHash, int TransientReasoningBytes, double ElapsedMs, double DiagnosticMs, int CounterCalls);
    private sealed class MeterProbe(ModelRouteKey route, PipeCounter counter) : IRequestTokenMeter
    {
        private readonly LocalReferenceRequestTokenMeter inner = new(route, counter.PolicyHash, counter);
        public List<MeasurementRow> Rows { get; } = []; public List<string[]> Offered { get; } = []; public int Dispatches;
        public ChatRequest? LastFullRequest { get; private set; }
        public TokenMeasurement Measure(ModelRouteKey current, ChatRequest request)
        {
            var before = counter.Metadata.Count; var started = Stopwatch.GetTimestamp(); var result = inner.Measure(current, request);
            var elapsed = Stopwatch.GetElapsedTime(started).TotalMilliseconds; var diagnosticsStarted = Stopwatch.GetTimestamp();
            var conservative = new RequestTokenMeter().Measure(current, request).InputTokens;
            var input = ChatRequestInputEnvelope.Write(current.ModelId, request); var inputHash = Hash(input); var wireHash = Hash(ChatRequestPayloadWriter.Write(current, request));
            using var json = JsonDocument.Parse(input);
            var reasoningBytes = json.RootElement.GetProperty("messages").EnumerateArray().Sum(m =>
                m.TryGetProperty("reasoning_content", out var reasoning) ? Encoding.UTF8.GetByteCount(reasoning.GetString()!) : 0);
            var diagnosticMs = Stopwatch.GetElapsedTime(diagnosticsStarted).TotalMilliseconds;
            Rows.Add(new(result.InputTokens, conservative, result.Kind.ToString(), request.Tools?.Count ?? 0, inputHash, wireHash,
                result.ReferencePolicyHash, reasoningBytes, elapsed, diagnosticMs, counter.Metadata.Count - before));
            Offered.Add(request.Tools?.Select(t => t.Name).ToArray() ?? []); return result;
        }
        public void Dispatch(ChatRequest request)
        {
            var measured = Measure(route, request); Check(measured.InputTokens <= 16000 && request.MaxOutputTokens == 2048, "LOCAL_REFERENCE_DISPATCH_LIMIT"); Dispatches++;
            if (request.Tools is { Count: > 0 }) LastFullRequest = request;
        }
        public void RecordUsage(ModelRouteKey current, ChatRequest request, ChatUsage usage) => inner.RecordUsage(current, request, usage);
        public void Invalidate(ModelRouteKey current) => inner.Invalidate(current);
    }
    private sealed class DeniedCredentials : FgoPet.Infrastructure.Secrets.ICredentialReader
    { public int Attempts; public Task<string?> ReadAsync(string target, CancellationToken token) { Attempts++; throw new ValidationFailure("LOCAL_REFERENCE_CREDENTIALS_FORBIDDEN"); } }
    private sealed class DeniedHttp : HttpMessageHandler
    { public int Attempts; protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) { Attempts++; throw new ValidationFailure("LOCAL_REFERENCE_HTTP_FORBIDDEN"); } }
    private sealed class InputGate(IRequestTokenMeter meter) : IChatDispatchGate
    {
        public int Admissions;
        public T Dispatch<T>(ChatRequest request, ModelRouteKey route, Func<T> start, CancellationToken token)
        {
            token.ThrowIfCancellationRequested(); var measured = meter.Measure(route, request);
            Check(measured.Kind == TokenCountKind.LocalReferenceEstimate && measured.InputTokens <= 16000, "LOCAL_REFERENCE_HTTP_INPUT_GUARD");
            Admissions++; return start();
        }
    }
    private sealed class SyntheticCredential : FgoPet.Infrastructure.Secrets.ICredentialReader
    { public Task<string?> ReadAsync(string target, CancellationToken token) => Task.FromResult<string?>("offline-fixture-only"); }
    private sealed class SyntheticHttp : HttpMessageHandler
    {
        public int Calls; public string? WireHash;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Calls++; WireHash = Hash(await request.Content!.ReadAsStringAsync(token));
            return new(System.Net.HttpStatusCode.OK) { Content = new StringContent("data: [DONE]\n\n", Encoding.UTF8, "text/event-stream") };
        }
    }
    private sealed class ValidationFailure(string code) : Exception(code);
}
