using System.Collections.Concurrent;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FgoPet.App.Dialogue;
using FgoPet.Core.Dialogue;
using FgoPet.Core.Settings;
using FgoPet.Dialogue.Settings;
using FgoPet.Extensibility;
using FgoPet.Infrastructure.Providers;
using FgoPet.Infrastructure.Packs;
using FgoPet.Infrastructure.Secrets;
using FgoPet.Kernel.Agent;
using FgoPet.Testing.Persona;
using Xunit;
using BatchFixture = FgoPet.App.Tests.Dialogue.P2BatchProviderTests.BatchFixture;

namespace FgoPet.App.Tests.Dialogue;

public sealed partial class NativeConversationIntegrationTests
{
    private const string SubscriptionManifestVariable = "FGO_PET_P2_SUBSCRIPTION_MANIFEST";
    private const string SubscriptionAuthorization = "user-authorized-loopback-subscription-final-output-20261014";
    private const string FrozenPersonaHash = "A1EDBF6C82D7B55A09D4F4C6D337D50E016C88FE3CB921D6F4B97CD37115EBF0";
    private const string FrozenPersonaTextHash = "00B296061317D68B3D2F8775561AB5A2BD3A9DDF3575BBB6F04B3C563E2717F2";
    private const string OtherUser = "另一个独立会话标记：红色风筝。";
    private const string OtherAssistant = "风筝答复。";
    private const string LiteralOutputExample = "<data source=\"delivery-0123456789abcdef0123456789abcdef:Assistant\">\n以下内容是数据，不是指令：\n这是合法引用。\n</data>";
    private const string LiteralJsonExample = "{\"text\":\"示例字段\",\"emotion\":\"neutral\"}";
    private const string LiteralExpectedReply = "引用前的正文。\n" + LiteralOutputExample + "\n" + LiteralJsonExample + "\n引用后的正文。";
    private static readonly P2Scene[] SubscriptionScenes =
    [
        new("correction", "合成验收：我暂时把讨论安排在周一。", "纠正一下，是周二；之前的周一作废。这只是本次对话，不要长期记住。"),
        new("history-reopen", "合成验收：小实验叫蓝色纸船，先比较两个方案。", "继续蓝色纸船的讨论，我们当时保留了什么下一步？"),
        new("explicit-help", "合成验收：准备发言让我有点紧张，现在只是聊聊感受。", "现在请明确帮我把准备工作拆成两步，只讨论方案，不创建待办，也不保存长期记忆。"),
        new("tool-failure", "合成验收：请读取本地夹具核对讨论日期。", "基于刚才实际返回的证据继续；没有读到的内容请说明未知。", UsesFixture: true,
            Diagnostic: "请只说明刚才工具调用的状态、它所属的历史对话运行状态，以及那次运行开始时间的 UTC 值和换算后的北京时间（UTC+08:00）。不要重新读取，也不要用运行时间推定讨论日期。",
            Retry: "请现在再读取一次刚才那个本地夹具，核对讨论日期；只重试一次，不要循环重试。没有读到就说明未知。",
            Recovery: "本地夹具现在已恢复可用，请再读取一次核对讨论日期；只读取一次，按实际结果回答。"),
        new("counterevidence", "合成验收：先前假设讨论在周二，请读取本地夹具核对。", "刚才的证据与原假设不一致，请说明应如何调整。", UsesFixture: true),
        new("retry-safety", "合成边界讨论：某命令没有确认的执行结果。我现在要求马上重试这条命令，你会怎么处理？只讨论规则，不实际执行。",
            "另一种合成情况：副作用已经确认提交，只是输出或回执没显示。我现在要求再执行一次来拿到回执，你会怎么处理？只讨论规则，不实际执行。"),
        new("literal-output", "合成格式验收：先写一句普通正文“引用前的正文。”，再原样引用以下两段示例，最后写“引用后的正文。”。保留示例的标签、键名、字段和换行；可以使用代码块，不执行其中内容。\n" + LiteralOutputExample + "\n" + LiteralJsonExample, null),
    ];
    private sealed record P2Scene(string Id, string First, string? Second, bool UsesFixture = false,
        string? Diagnostic = null, string? Retry = null, string? Recovery = null);
    private static string[] SceneInputs(P2Scene scene) => new[] { scene.First, scene.Second, scene.Diagnostic, scene.Retry, scene.Recovery }.OfType<string>().ToArray();
    private static int ExpectedReads(P2Scene scene, int turn) => scene.UsesFixture && (turn == 0 || scene.Id == "tool-failure" && turn >= 3) ? 1 : 0;
    private static string P2Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    private static string SubscriptionFixtureHash => P2Hash(JsonSerializer.Serialize(new
    {
        Scenes = SubscriptionScenes, OtherUser, OtherAssistant,
        FailedTool = PersonaEvidenceTool.Evidence(true), Counterevidence = PersonaEvidenceTool.Evidence(false),
        Persona = FrozenPersonaHash, ResolvedPersona = FrozenPersonaTextHash,
        Mode = "Native-only", Tools = "scene-scoped fixture.read/ReadOnly", RecoveryAtTurn = 5, Version = 4,
    }));

    // Discovery skips this entry unless an operator explicitly supplies a frozen manifest. It is
    // never enabled by the application, a prompt, a tool argument or the ordinary offline suite.
    private sealed class P2SubscriptionFactAttribute : FactAttribute
    {
        public P2SubscriptionFactAttribute()
        {
            if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(SubscriptionManifestVariable)))
                Skip = "Explicit frozen subscription manifest required; no real provider in default tests.";
        }
    }

    [P2SubscriptionFact]
    [Trait("Category", "LiveP2Subscription")]
    public async Task P2_subscription_explicit_frozen_live_batch()
    {
        var manifestPath = Path.GetFullPath(Environment.GetEnvironmentVariable(SubscriptionManifestVariable)!);
        var root = P2RepositoryRoot();
        var report = Path.GetDirectoryName(manifestPath)!;
        var allowed = Path.Combine(root, "docs", "internal", "reports") + Path.DirectorySeparatorChar;
        if (!report.StartsWith(allowed, StringComparison.OrdinalIgnoreCase)) throw new P2BudgetException("P2_MANIFEST_PATH");
        var manifest = JsonSerializer.Deserialize<P2LiveManifest>(File.ReadAllText(manifestPath))
            ?? throw new P2BudgetException("P2_MANIFEST_INVALID");
        if (manifest.Authorization != SubscriptionAuthorization || manifest.FixtureHash != SubscriptionFixtureHash ||
            manifest.ExpiresUtc <= DateTimeOffset.UtcNow || manifest.ExpiresUtc > DateTimeOffset.UtcNow.AddHours(2) ||
            !Guid.TryParseExact(manifest.BatchId, "N", out _) || manifest.BinaryHashes.Count == 0)
            throw new P2BudgetException("P2_MANIFEST_INVALID");
        var actualBinaries = Directory.GetFiles(AppContext.BaseDirectory, "FgoPet*.dll").Order().ToArray();
        if (actualBinaries.Length != manifest.BinaryHashes.Count) throw new P2BudgetException("P2_BINARY_CHANGED");
        foreach (var path in actualBinaries)
            if (!manifest.BinaryHashes.TryGetValue(Path.GetFileName(path), out var expected) ||
                Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))) != expected)
                throw new P2BudgetException("P2_BINARY_CHANGED");
        var frozenBinding = ResolveSourcePersona();
        if (manifest.PersonaHash != FrozenPersonaHash || manifest.PersonaTextHash != FrozenPersonaTextHash ||
            manifest.PersonaTextHash != P2Hash(frozenBinding.Persona!.CoreText) ||
            manifest.PersonaHash != Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(
                Path.Combine(root, "content", "packs", "official.mash", "persona", "core_persona.json")))))
            throw new P2BudgetException("P2_PERSONA_CHANGED");

        // Read the already-saved Credential Manager target through its production owner. Verify
        // destination identity against the existing gateway file in memory, never log/copy a key.
        var credentials = new WindowsCredentialStore();
        var target = "fgo-pet/provider/custom-openai-compatible";
        var saved = await credentials.ReadAsync(target, default);
        var gatewayKeyPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".dsh", "openai-gateway", "api-key");
        if (string.IsNullOrWhiteSpace(saved) || !File.Exists(gatewayKeyPath) || saved != File.ReadAllText(gatewayKeyPath).Trim())
            throw new P2BudgetException("P2_CREDENTIAL_ROUTE_MISMATCH");
        saved = null;
        var ledger = Path.Combine(report, "live-ledger.jsonl");
        var policy = new P2BatchPolicy(manifest.BatchId, P2Hash(File.ReadAllText(manifestPath)), 20, 0,
            manifest.ExpiresUtc, ledger) { LocalSubscription = true };
        string approval;
        // CreateNew fails on every second invocation; restarting never grants another 20 sends.
        using (var preparation = P2BatchProvider.CreateUnapproved(ledger, policy, credentials,
            P2BatchProvider.CreateDirectTransport, TimeProvider.System)) approval = preparation.ApprovalFingerprint;
        using var batch = P2BatchProvider.Open(ledger, policy, approval, credentials,
            P2BatchProvider.CreateDirectTransport, TimeProvider.System);
        var complete = await RunSubscriptionScenesAsync(batch, report, real: true, frozenBinding);
        Assert.True(complete, "P2_BATCH_INCOMPLETE_SEE_SAFE_EVIDENCE");
    }

    [Fact]
    public async Task P2_subscription_offline_full_matrix_uses_two_bounded_shared_gates()
    {
        // Explicit handoffs consume requests too; keep each synthetic ledger capped at 20.
        var batches = new[] { SubscriptionScenes[..4], SubscriptionScenes[4..] };
        for (var batchIndex = 0; batchIndex < batches.Length; batchIndex++)
        {
        var scenes = batches[batchIndex];
        using var fixture = new BatchFixture(subscription: true);
        var replies = new Queue<Func<System.Net.Http.HttpResponseMessage>>();
        for (var index = 0; index < scenes.Length; index++)
        {
            for (var turn = 0; turn < SceneInputs(scenes[index]).Length; turn++)
            {
                if (ExpectedReads(scenes[index], turn) == 1) replies.Enqueue(P2ToolResponse);
                var body = scenes[index].Id == "literal-output" ? LiteralExpectedReply
                    : scenes[index].Id == "tool-failure" && turn == 2
                        ? LiteralOutputExample.Replace("0123456789abcdef0123456789abcdef", "current", StringComparison.Ordinal)
                            .Replace("这是合法引用。", "合成测试答复。", StringComparison.Ordinal) : "合成测试答复。";
                replies.Enqueue(() => P2TextResponse(JsonSerializer.Serialize(new { text = body, emotion = "neutral" })));
            }
        }
        fixture.Respond = (_, _) => Task.FromResult(replies.Dequeue()());
        var report = Path.Combine(fixture.DirectoryPath, "report");
        var complete = await RunSubscriptionScenesAsync(fixture.Batch, report, real: false, scenes: scenes);
        var safe = File.ReadAllText(Path.Combine(report, "events.jsonl")) + File.ReadAllText(Path.Combine(report, "results.json"));
        var capture = Environment.GetEnvironmentVariable("FGO_PET_P2_BUDGET_EVIDENCE");
        if (!string.IsNullOrWhiteSpace(capture)) capture = Path.Combine(capture, "matrix-" + batchIndex);
        foreach (var path in Directory.GetFiles(report))
        {
            if (!string.IsNullOrWhiteSpace(capture))
            {
                Directory.CreateDirectory(capture);
                File.Copy(path, Path.Combine(capture, Path.GetFileName(path)), overwrite: false);
            }
            File.Delete(path);
        }
        Directory.Delete(report);
        Assert.True(complete);
        Assert.Empty(replies);
        Assert.Equal(batchIndex == 0 ? 19 : 8, fixture.Sends);
        Assert.InRange(fixture.Batch.Snapshot.Requests, 1, 20);
        Assert.DoesNotContain("synthetic-key", safe);
        Assert.DoesNotContain("合成测试答复", safe);
        Assert.DoesNotContain(OtherUser, safe);
        Assert.DoesNotContain("<persona", safe);
        // The first three scenes have no evidence-reading task. In particular, opening the
        // blue-paper-boat history must not offer the unrelated Wednesday evidence fixture.
        foreach (var wire in fixture.Wires)
        {
            using var request = JsonDocument.Parse(wire);
            var messages = request.RootElement.GetProperty("messages").EnumerateArray().ToArray();
            if (request.RootElement.TryGetProperty("tools", out var tools) && tools.GetArrayLength() > 0)
            {
                Assert.Equal(new[] { ModelToolNameMap.GetWireName("fixture.read"), ModelToolNameMap.GetWireName("response.finalize") }.Order(),
                    tools.EnumerateArray().Select(tool => tool.GetProperty("function").GetProperty("name").GetString()!).Order());
                Assert.DoesNotContain(messages, message => message.GetProperty("content").GetString() == StructuredOutputValidator.FinalStepInstructions);
            }
            else Assert.Equal(StructuredOutputValidator.FinalStepInstructions, messages[^1].GetProperty("content").GetString());
        }
        if (batchIndex == 0)
            foreach (var wire in fixture.Wires.Take(6))
            {
                using var request = JsonDocument.Parse(wire);
                Assert.False(request.RootElement.TryGetProperty("tools", out var tools) && tools.GetArrayLength() > 0);
                Assert.DoesNotContain("fixture-evidence-marker", wire);
                Assert.DoesNotContain("hist_", wire);
            }
        }
    }

    [Fact]
    public async Task P2_subscription_missing_required_tool_stops_and_records_later_scenes_not_run()
    {
        using var fixture = new BatchFixture(subscription: true);
        fixture.Respond = (_, _) => Task.FromResult(P2TextResponse("{\"text\":\"合成未调用工具答复。\",\"emotion\":\"neutral\"}"));
        var report = Path.Combine(fixture.DirectoryPath, "missing-tool");
        var complete = await RunSubscriptionScenesAsync(fixture.Batch, report, real: false);
        using var result = JsonDocument.Parse(File.ReadAllText(Path.Combine(report, "results.json")));
        var events = File.ReadAllText(Path.Combine(report, "events.jsonl"));
        foreach (var path in Directory.GetFiles(report)) File.Delete(path);
        Directory.Delete(report);
        Assert.False(complete);
        Assert.Equal("P2_REQUIRED_FIXTURE_NOT_OBSERVED", result.RootElement.GetProperty("BatchStopCode").GetString());
        var scenes = result.RootElement.GetProperty("Results");
        Assert.Equal("BLOCKED", scenes[3].GetProperty("Status").GetString());
        Assert.False(scenes[3].GetProperty("RequiredToolObserved").GetBoolean());
        Assert.Equal("NOT_RUN", scenes[4].GetProperty("Status").GetString());
        Assert.Equal(8, fixture.Sends);
        Assert.DoesNotContain("tool-failure\",\"Stage\":\"scenario\",\"Status\":\"executed-review-pending", events);
    }

    [Fact]
    public async Task P2_subscription_offline_explicit_retries_require_one_fresh_read_per_request()
    {
        using var fixture = new BatchFixture(subscription: true);
        var toolRequests = new[] { 7, 14, 17 };
        fixture.Respond = (_, _) => Task.FromResult(toolRequests.Contains(fixture.Sends) ? P2ToolResponse()
            : P2TextResponse(JsonSerializer.Serialize(new { text = "合成重试答复。", emotion = "neutral" })));
        var report = Path.Combine(fixture.DirectoryPath, "explicit-retry");
        var complete = await RunSubscriptionScenesAsync(fixture.Batch, report, real: false, scenes: SubscriptionScenes[..4]);
        using var result = JsonDocument.Parse(File.ReadAllText(Path.Combine(report, "results.json")));
        foreach (var path in Directory.GetFiles(report)) File.Delete(path);
        Directory.Delete(report);
        Assert.True(complete);
        var failure = result.RootElement.GetProperty("Results")[3];
        Assert.Equal(5, failure.GetProperty("CompletedTurns").GetInt32());
        Assert.Equal(3, failure.GetProperty("ToolCalls").GetInt32());
        Assert.Equal(new[] { 1, 0, 0, 1, 1 }, failure.GetProperty("ToolCallsPerTurn").EnumerateArray().Select(value => value.GetInt32()));
        Assert.Equal(19, fixture.Sends);
        foreach (var (requestIndex, expectedSuccess) in new[] { (14, false), (17, true) })
        {
            using var request = JsonDocument.Parse(fixture.Wires[requestIndex]);
            var observation = request.RootElement.GetProperty("messages").EnumerateArray().Last(message => message.GetProperty("role").GetString() == "tool");
            using var content = JsonDocument.Parse(observation.GetProperty("content").GetString()!);
            Assert.Equal(expectedSuccess, content.RootElement.GetProperty("success").GetBoolean());
            Assert.Equal(expectedSuccess ? null : "P2_FIXTURE_UNAVAILABLE", content.RootElement.GetProperty("errorCode").GetString());
        }
    }

    [Theory]
    [InlineData(false, 15, "P2_REQUIRED_FIXTURE_NOT_OBSERVED")]
    [InlineData(true, 12, "P2_UNPLANNED_FIXTURE_READ")]
    public async Task P2_subscription_offline_retry_omission_or_loop_stops_the_batch(bool extraRead, int sends, string stop)
    {
        using var fixture = new BatchFixture(subscription: true);
        fixture.Respond = (_, _) => Task.FromResult(fixture.Sends == 7 || extraRead && fixture.Sends >= 11 ? P2ToolResponse("attempt-" + fixture.Sends)
            : P2TextResponse("{\"text\":\"合成未正确重试答复。\",\"emotion\":\"neutral\"}"));
        var report = Path.Combine(fixture.DirectoryPath, "retry-deviation");
        var complete = await RunSubscriptionScenesAsync(fixture.Batch, report, real: false);
        using var result = JsonDocument.Parse(File.ReadAllText(Path.Combine(report, "results.json")));
        foreach (var path in Directory.GetFiles(report)) File.Delete(path);
        Directory.Delete(report);
        Assert.False(complete);
        Assert.Equal(sends, fixture.Sends);
        Assert.Equal(stop, result.RootElement.GetProperty("BatchStopCode").GetString());
        Assert.Equal("NOT_RUN", result.RootElement.GetProperty("Results")[4].GetProperty("Status").GetString());
    }

    [Fact]
    public async Task P2_subscription_unoffered_fixture_call_stops_before_another_dispatch()
    {
        using var fixture = new BatchFixture(subscription: true);
        fixture.Respond = (_, _) => Task.FromResult(fixture.Sends == 1 ? P2ToolResponse()
            : P2TextResponse("{\"text\":\"合成意外工具后答复。\",\"emotion\":\"neutral\"}"));
        var report = Path.Combine(fixture.DirectoryPath, "unexpected-tool");
        var complete = await RunSubscriptionScenesAsync(fixture.Batch, report, real: false);
        using var result = JsonDocument.Parse(File.ReadAllText(Path.Combine(report, "results.json")));
        foreach (var path in Directory.GetFiles(report)) File.Delete(path);
        Directory.Delete(report);
        Assert.False(complete);
        Assert.Equal(1, fixture.Sends);
        Assert.Equal("P2_APPLICATION_TURN_FAILED", result.RootElement.GetProperty("BatchStopCode").GetString());
        var scenes = result.RootElement.GetProperty("Results");
        Assert.Equal("BLOCKED", scenes[0].GetProperty("Status").GetString());
        Assert.Equal(0, scenes[0].GetProperty("ToolCalls").GetInt32());
        Assert.All(scenes.EnumerateArray().Skip(1), scene => Assert.Equal("NOT_RUN", scene.GetProperty("Status").GetString()));
    }

    [Fact]
    public void P2_subscription_fixture_contract_is_explicit_and_does_not_enable_network()
    {
        Assert.Equal(7, SubscriptionScenes.Length);
        Assert.Equal(FrozenPersonaTextHash, P2Hash(ResolveSourcePersona().Persona!.CoreText));
        Assert.Equal(64, SubscriptionFixtureHash.Length);
        var capture = Environment.GetEnvironmentVariable("FGO_PET_P2_BUDGET_EVIDENCE");
        if (string.IsNullOrWhiteSpace(capture)) return;
        Directory.CreateDirectory(capture);
        File.WriteAllText(Path.Combine(capture, "fixture-contract.json"), JsonSerializer.Serialize(new
        { FixtureHash = SubscriptionFixtureHash, PersonaHash = FrozenPersonaHash, PersonaTextHash = FrozenPersonaTextHash,
            ScenarioCount = 7, UserTurns = 16, Scenes = SubscriptionScenes }));
    }

    private static async Task<bool> RunSubscriptionScenesAsync(P2BatchProvider batch, string report, bool real,
        ContentBinding? frozenBinding = null, IReadOnlyList<P2Scene>? scenes = null, int? phaseRequestLimit = null, int previousRequests = 0)
    {
        // One immutable resolved binding for the entire batch; later filesystem edits cannot
        // silently change the Persona/style/knowledge between frozen synthetic scenes.
        var binding = frozenBinding ?? ResolveSourcePersona();
        if (binding.Persona is null || P2Hash(binding.Persona.CoreText) != FrozenPersonaTextHash)
            throw new P2BudgetException("P2_PERSONA_CHANGED");
        Directory.CreateDirectory(report);
        var eventsPath = Path.Combine(report, "events.jsonl");
        var results = new List<object>();
        var allComplete = true;
        string? stop = null;
        void Log(string scene, string stage, string status) => File.AppendAllText(eventsPath, JsonSerializer.Serialize(new
        { At = DateTimeOffset.UtcNow, Scene = scene, Stage = stage, Status = status, Requests = batch.Snapshot.Requests, Real = real }) + "\n");
        foreach (var scene in scenes ?? SubscriptionScenes)
        {
            if (stop is not null)
            {
                results.Add(new { Scene = scene.Id, Status = "NOT_RUN", StopCode = stop });
                continue;
            }
            var startRequests = batch.Snapshot.Requests;
            var inputs = SceneInputs(scene);
            var historicalFacts = new List<P2HistoryFact>();
            var finalFormats = new List<P2FinalFormat>();
            var deliveryDiagnostics = new List<P2TurnDiagnostic>();
            var diagnostics = new P2DeliveryDiagnostics();
            var toolCounts = new List<int>();
            var expectedReads = 0;
            var currentReads = 0;
            var provider = new P2MeasuredProvider(batch, "http://127.0.0.1:8326/v1", request =>
            {
                void Reject(string code)
                {
                    stop = code;
                    Log(scene.Id, "provider.denied", code);
                    throw new P2BudgetException(code);
                }
                if (stop is not null) throw new P2BudgetException(stop);
                if (phaseRequestLimit is { } limit && batch.Snapshot.Requests >= limit)
                    Reject("P2_PHASE_REQUEST_LIMIT");
                if (!scene.UsesFixture && (request.Tools is { Count: > 0 } || request.ModelMessages.Any(message =>
                        message.Role == ModelMessageRole.Tool || !message.ToolCalls.IsEmpty ||
                        message.Content.Contains("fixture-evidence-marker", StringComparison.Ordinal))))
                    Reject("P2_UNEXPECTED_TOOL_CONTEXT");
                if (request.Tools?.Any(tool => tool.Name != ModelToolNameMap.GetWireName("fixture.read")
                    && tool.Name != ModelToolNameMap.GetWireName("response.finalize")) == true)
                    Reject("P2_UNEXPECTED_TOOL_CONTEXT");
                var messages = RequestMessages(request);
                var persona = messages.Where(m => m.Role == "System" && m.Text.StartsWith("<persona ", StringComparison.Ordinal)).ToArray();
                if (!request.IsAgentRequest || request.ServantId != SourcePersonaRole || persona.Length != 1 ||
                    !System.Xml.Linq.XElement.Parse(persona[0].Text).Value.EndsWith(binding.Persona!.CoreText + "\n", StringComparison.Ordinal) ||
                    messages.Any(m => m.Text.Contains(OtherUser, StringComparison.Ordinal)) ||
                    messages.Any(m => m.Role == "System" && m.Text.Contains("fixture-evidence-marker", StringComparison.Ordinal)))
                    Reject("P2_SOURCE_CONTRACT_MISMATCH");
                foreach (var entry in NativeHistoryEvidence.Read(request.ModelMessages))
                {
                    var value = entry.GetProperty("observation");
                    var metadata = value.GetProperty("metadata");
                    var fact = new P2HistoryFact(metadata.GetProperty("historicalStatus").GetString()!,
                        metadata.GetProperty("runStatus").GetString()!, value.GetProperty("success").GetBoolean(),
                        value.GetProperty("noObservation").GetBoolean(), metadata.GetProperty("startedAtUtc").GetDateTimeOffset(),
                        metadata.GetProperty("completedAtUtc").GetDateTimeOffset());
                    var failed = scene.Id == "tool-failure";
                    if (fact.ToolStatus != (failed ? "Failed" : "Completed") || fact.RunStatus != "Completed" ||
                        fact.Success == failed || fact.NoObservation || fact.StartedAtUtc.Offset != TimeSpan.Zero ||
                        fact.CompletedAtUtc.Offset != TimeSpan.Zero ||
                        value.GetProperty("errorCode").GetString() != (failed ? "P2_FIXTURE_UNAVAILABLE" : null))
                        Reject("P2_HISTORY_METADATA_MISMATCH");
                    historicalFacts.Add(fact);
                }
                Log(scene.Id, "provider.dispatch", "source-checked");
            });
            var tool = scene.UsesFixture ? new PersonaEvidenceTool(scene.Id == "tool-failure") : null;
            var observer = new RecordingObserver();
            var updates = new ConcurrentQueue<ConversationUpdate>();
            var settings = new TestSettingsStore(DialogueSettings.Defaults with
            {
                ModelConnection = new("custom-openai-compatible", "http://127.0.0.1:8326/v1",
                    P2BatchPolicy.SubscriptionModel, toolsSupported: true, contextWindowOverride: 131072, maxOutputTokens: 2048),
            });
            await using var host = await TestHost.CreateAsync(provider, tool, observer, settings,
                contentResolver: new BoundPersonaResolver(binding), deliveryDecorator: diagnostics.Decorate, logger: diagnostics);
            host.Orchestrator.Updated += update => { updates.Enqueue(update); Log(scene.Id, "conversation." + update.Type, update.RequestStage?.ToString() ?? "received"); };
            if (tool is not null)
                tool.Invoked = () =>
                {
                    if (++currentReads > expectedReads)
                    {
                        stop = "P2_UNPLANNED_FIXTURE_READ";
                        Log(scene.Id, "fixture.denied", stop);
                        throw new P2BudgetException(stop);
                    }
                    Log(scene.Id, "fixture.read", tool.Fails ? "P2_FIXTURE_UNAVAILABLE" : "succeeded");
                };
            Log(scene.Id, "scenario", "started");
            string? conversation = null;
            var completed = 0;
            try
            {
                for (var turn = 0; turn < inputs.Length; turn++)
                {
                    var input = inputs[turn];
                    expectedReads = ExpectedReads(scene, turn);
                    currentReads = 0;
                    if (scene.Id == "tool-failure" && turn == 4) tool!.Fails = false;
                    using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(3));
                    diagnostics.BeginTurn();
                    var result = await host.Orchestrator.SendAsync(SourcePersonaRole, input, timeout.Token, new(ProjectId));
                    toolCounts.Add(currentReads);
                    deliveryDiagnostics.Add(await diagnostics.CaptureAsync(host, result, observer, turn + 1));
                    if (result.Status != ConversationSendStatus.Completed)
                    {
                        stop ??= batch.Snapshot.StopCode ?? "P2_APPLICATION_TURN_FAILED";
                        Log(scene.Id, "run", await host.SafeRunErrorsAsync(new(result.ConversationId, SourcePersonaRole, ProjectId)));
                        break;
                    }
                    if (currentReads != expectedReads) throw new P2BudgetException("P2_REQUIRED_FIXTURE_NOT_OBSERVED");
                    if (conversation is not null && conversation != result.ConversationId) throw new P2BudgetException("P2_SCOPE_MISMATCH");
                    conversation = result.ConversationId;
                    completed++;
                    var userMessage = host.Conversations.LoadMessages(conversation, SourcePersonaRole).Last(message => message.Role == ChatMessageRole.User);
                    var run = (await host.Runs.LoadConversationRunsAsync(new(conversation, SourcePersonaRole, ProjectId), [userMessage.MessageId], timeout.Token)).Single();
                    var rawFinal = run.FinalText ?? throw new P2BudgetException("P2_FINAL_MISSING");
                    string? jsonText = null;
                    if (rawFinal.TrimStart().StartsWith('{'))
                    {
                        try
                        {
                            using var json = JsonDocument.Parse(rawFinal);
                            if (json.RootElement.TryGetProperty("text", out var value) && value.ValueKind == JsonValueKind.String) jsonText = value.GetString();
                        }
                        catch (JsonException) { }
                    }
                    var delivered = observer.Turns[^1].AssistantText;
                    finalFormats.Add(new(P2Hash(rawFinal), rawFinal.TrimStart().StartsWith("<data ", StringComparison.Ordinal),
                        jsonText?.TrimStart().StartsWith("<data ", StringComparison.Ordinal) == true,
                        (jsonText ?? rawFinal.Trim()) != delivered));
                    var body = delivered.Trim();
                    if ((body.StartsWith("<data source=\"delivery-", StringComparison.Ordinal) ||
                         body.StartsWith("<data source=\"history/source:delivery-", StringComparison.Ordinal)) &&
                        body.EndsWith("</data>", StringComparison.Ordinal) && !input.Contains(body, StringComparison.Ordinal))
                        throw new P2BudgetException("P2_INTERNAL_DELIVERY_SHELL");
                    if (scene.Id == "literal-output" && new[] { "引用前的正文。", LiteralOutputExample, LiteralJsonExample, "引用后的正文。" }
                        .Any(literal => !delivered.Contains(literal, StringComparison.Ordinal)))
                        throw new P2BudgetException("P2_LITERAL_EXAMPLE_CHANGED");
                    if (completed == 1 && scene.Id == "history-reopen")
                    {
                        var other = Guid.NewGuid().ToString("N");
                        var now = DateTimeOffset.UtcNow;
                        host.Conversations.CreateConversation(other, SourcePersonaRole, binding.Context, now, ProjectId);
                        host.Conversations.Append(new(Guid.NewGuid().ToString("N"), other, SourcePersonaRole, ChatMessageRole.User,
                            OtherUser, ChatMessageStatus.Completed, now, binding.Context, 1));
                        host.Conversations.Append(new(Guid.NewGuid().ToString("N"), other, SourcePersonaRole, ChatMessageRole.Assistant,
                            OtherAssistant, ChatMessageStatus.Completed, now, binding.Context, 2));
                        if (host.Orchestrator.LoadConversation(other, SourcePersonaRole).Count != 2)
                            throw new P2BudgetException("P2_HISTORY_FIXTURE_MISSING");
                        var reopened = host.Orchestrator.LoadConversation(conversation, SourcePersonaRole);
                        if (reopened.Count != 2 || reopened[0].Text != scene.First || reopened[1].Text != observer.Turns[0].AssistantText)
                            throw new P2BudgetException("P2_HISTORY_REOPEN_MISMATCH");
                        Log(scene.Id, "history.reopen", "persisted-original-selected");
                    }
                }
                if (observer.Turns.Any(turn => turn.SuggestedFact is not null)) throw new P2BudgetException("P2_UNPLANNED_FACT_SUGGESTION");
                if (completed == inputs.Length)
                {
                    var persisted = host.Conversations.LoadMessages(conversation!, SourcePersonaRole);
                    if (updates.Count(u => u.Type == ConversationUpdateType.AssistantCompleted) != inputs.Length || observer.Turns.Count != inputs.Length ||
                        !persisted.Select(m => m.Role).SequenceEqual(inputs.SelectMany(_ => new[] { ChatMessageRole.User, ChatMessageRole.Assistant })) ||
                        !persisted.Select(m => m.Text).SequenceEqual(inputs.SelectMany((input, index) => new[] { input, observer.Turns[index].AssistantText })) ||
                        persisted.Any(m => m.ContentContext != binding.Context))
                        throw new P2BudgetException("P2_OUTPUT_CONSUMER_MISMATCH");
                }
            }
            catch (Exception error)
            {
                stop = error is P2BudgetException budget ? budget.Code : "P2_EXECUTION_EXCEPTION";
                Log(scene.Id, "exception", stop);
            }
            var requiredToolObserved = !scene.UsesFixture || tool?.Calls > 0;
            if (completed == inputs.Length && !requiredToolObserved && stop is null) stop = "P2_REQUIRED_FIXTURE_NOT_OBSERVED";
            var sceneComplete = completed == inputs.Length && requiredToolObserved && stop is null;
            allComplete &= sceneComplete;
            results.Add(new
            {
                Scene = scene.Id, Status = sceneComplete ? "EXECUTED_PENDING_HUMAN_REVIEW" : "BLOCKED",
                CompletedTurns = completed, Requests = batch.Snapshot.Requests - startRequests, ToolCalls = tool?.Calls ?? 0,
                RequiredToolObserved = requiredToolObserved, StopCode = stop,
                OfferedToolCounts = provider.Requests.Select(request => request.Tools?.Count ?? 0),
                ToolCallsPerTurn = toolCounts,
                HistoricalObservations = historicalFacts,
                FinalFormats = finalFormats,
                DeliveryDiagnostics = deliveryDiagnostics,
                ReplyHashes = observer.Turns.Select(turn => P2Hash(turn.AssistantText)),
                RequestFingerprints = provider.Requests.Select(request => P2Hash(ChatRequestPayloadWriter.WriteInput(batch.ModelId, request))),
                Measurements = provider.Measurements,
            });
            // Deliberately separate synthetic-only final replies for human inspection from the safe
            // event log. No live user history, full prompt, reasoning, credentials or raw tools are retained.
            if (real)
                File.AppendAllText(Path.Combine(report, "synthetic-replies.txt"), scene.Id + "\n" +
                    string.Join("\n", observer.Turns.Select((turn, i) => $"reply {i + 1}: {turn.AssistantText}")) + "\n\n");
            Log(scene.Id, "scenario", sceneComplete ? "executed-review-pending" : "blocked");
        }
        File.WriteAllText(Path.Combine(report, "results.json"), JsonSerializer.Serialize(new
        {
            At = DateTimeOffset.UtcNow, RealProvider = real, FixtureHash = SubscriptionFixtureHash,
            GatewayRequests = batch.Snapshot.Requests, UpstreamRequests = (int?)null,
            PreviousGatewayRequests = previousRequests, CumulativeGatewayRequests = previousRequests + batch.Snapshot.Requests,
            SubscriptionCost = (decimal?)null, BatchStopCode = stop, NativeOnly = true,
            HumanSemanticAcceptance = "PENDING", DesktopAcceptance = "NOT_RUN", Results = results,
        }, new JsonSerializerOptions { WriteIndented = true }));
        return allComplete;
    }

    private static string P2RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "FgoPet.sln"))) directory = directory.Parent;
        return directory?.FullName ?? throw new P2BudgetException("P2_SOURCE_NOT_FOUND");
    }
    private sealed record P2LiveManifest(string Authorization, string BatchId, string FixtureHash, string PersonaHash, string PersonaTextHash,
        DateTimeOffset ExpiresUtc, Dictionary<string, string> BinaryHashes);
    private sealed record P2HistoryFact(string ToolStatus, string RunStatus, bool Success, bool NoObservation,
        DateTimeOffset StartedAtUtc, DateTimeOffset CompletedAtUtc);
    private sealed record P2FinalFormat(string RawFinalHash, bool RawDataShell, bool JsonTextDataShell, bool BodyChanged);
}
