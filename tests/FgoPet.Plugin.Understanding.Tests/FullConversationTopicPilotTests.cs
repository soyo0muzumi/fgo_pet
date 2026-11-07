using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FgoPet.App.Dialogue;
using FgoPet.Core.Dialogue;
using FgoPet.Core.Settings;
using FgoPet.Extensibility;
using FgoPet.Infrastructure.Persistence;
using FgoPet.Infrastructure.Providers;
using FgoPet.Kernel.Agent;
using FgoPet.Understanding;
using FgoPet.Understanding.Contracts;
using Microsoft.Data.Sqlite;
using Xunit;

namespace FgoPet.Plugin.Understanding.Tests;

public sealed class FullConversationTopicPilotTests : IDisposable
{
    private readonly string _dbDir;
    private readonly RuntimeDatabase _db;
    private readonly ConversationScope _scope = new("mash", "test-project");

    public FullConversationTopicPilotTests()
    {
        _dbDir = Path.Combine(Path.GetTempPath(), "fgo-full-conv-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dbDir);
        _db = new RuntimeDatabase(Path.Combine(_dbDir, "runtime.db"), pooling: false);
        new RuntimeDatabaseMigrator(_db).Migrate();
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dbDir, true); } catch { /* ignore */ }
    }

    [Fact]
    public async Task UnderCapacity_FullConversation_40Messages_IncludesHeadAndTailInSameRequest_SavesShortTermTopic()
    {
        var timeline = new PagedTimeline();
        // 12 messages: Msg 2 is topic introduction, Msg 12 is tail correction
        timeline.AddUserMessage("Hello Mashu, can we discuss some research?");
        timeline.AddUserMessage("I want to research quantum computing with superconducting circuits.");
        timeline.AddAssistantMessage("Superconducting circuits are a leading platform for quantum processors.");
        for (var i = 4; i <= 39; i++)
        {
            if (i % 2 == 0) timeline.AddUserMessage($"Can you elaborate on detail {i}?");
            else timeline.AddAssistantMessage($"Here are details regarding aspect {i}.");
        }
        timeline.AddUserMessage("其实是更正：我的兴趣具体转向了拓扑量子纠错，而不是超导量子比特。");

        var topicStore = new ConversationTopicStore(_db);
        var mockProvider = new ReviewChatProvider(req =>
        {
            var binding = ExtractBinding(req);
            return $$"""
            {
              "schema": "theme_analysis_v1",
              "request": "{{binding}}",
              "interests": [
                {
                  "topic": "拓扑量子纠错",
                  "attribution": "user",
                  "tense": "current",
                  "basis_statement": "I want to research quantum computing with superconducting circuits.",
                  "counter_evidence": "其实是更正：我的兴趣具体转向了拓扑量子纠错，而不是超导量子比特。",
                  "research_candidate": true,
                  "research_rationale": "前沿量子物理主题，适合文献检索",
                  "sources": ["s2", "s40"]
                }
              ]
            }
            """;
        });

        var route = ModelRouteKey.From(new ModelConnectionSettings("deepseek", "https://api.deepseek.com/v1", "deepseek-flash", maxOutputTokens: 2048));
        var meter = new RequestTokenMeter();
        var model = new UnderstandingReviewModel(mockProvider, route, meter, requestLimits: new(128000, 2048));
        var factory = new ReviewModelFactory(model);
        var owner = new UnderstandingService(_db, timeline);
        owner.Configure(_scope, 0, new(UnderstandingMode.Proposals, ReadSources: true, SaveCandidates: true, ExternalProcessing: true, UseInDialogue: true));
        var lifetime = new DialogueContextLifetime();

        var service = new UnderstandingReviewService(
            owner, timeline, timeline, factory, lifetime, TimeProvider.System,
            topicStore: topicStore);

        // Trigger review from the first head only.
        var receipt = await service.ManualAsync(_scope, "pilot-full-conv-op", timeline.Heads.Take(1).ToArray());
        Assert.NotNull(receipt);

        // Verify all 40 originals are supplied in order.
        var request = Assert.Single(mockProvider.Requests);
        using var doc = JsonDocument.Parse(request.ModelMessages[^1].Content);
        var sourcesArray = doc.RootElement.GetProperty("sources").EnumerateArray().ToList();
        Assert.Equal(40, sourcesArray.Count);
        Assert.Equal("s1", sourcesArray[0].GetProperty("alias").GetString());
        Assert.Equal("s40", sourcesArray[39].GetProperty("alias").GetString());
        Assert.Contains("superconducting circuits", sourcesArray[1].GetProperty("text").GetString()!);
        Assert.Contains("拓扑量子纠错", sourcesArray[39].GetProperty("text").GetString()!);

        // Verify topic was stored in IShortTermTopicStore
        var activeTopics = topicStore.GetActiveTopics(_scope);
        var savedTopic = Assert.Single(activeTopics);
        Assert.Equal("拓扑量子纠错", savedTopic.Topic);
        Assert.Equal("其实是更正：我的兴趣具体转向了拓扑量子纠错，而不是超导量子比特。", savedTopic.Correction);
        Assert.Equal("user", savedTopic.Attribution);
        Assert.Equal("current", savedTopic.Tense);
        Assert.True(savedTopic.ResearchCandidate);
        Assert.Equal(1, savedTopic.Revision);
        Assert.True(savedTopic.ActiveUntil > DateTimeOffset.UtcNow);
        Assert.Equal(2, savedTopic.Sources.Count);
        Assert.Equal(timeline.Heads[1].Source.Fingerprint, savedTopic.Sources[0]);
        Assert.Equal(timeline.Heads[39].Source.Fingerprint, savedTopic.Sources[1]);
    }

    [Fact]
    public async Task OverCapacity_CompressesEarlierContext_RetainsRecentTurnsAndKeyCorrection_MarksSummary_Remeasures()
    {
        var originals = new List<ReviewOriginal>();
        for (var i = 1; i <= 10; i++)
        {
            var text = i == 6
                ? "更正：前面提到的参数不对，换成0.75"
                : $"Conversation message content item number {i} with some descriptive tokens. " + new string('x', 900);
            originals.Add(CreateOriginal(i, text));
        }

        var mockProvider = new ReviewChatProvider(req =>
        {
            var binding = ExtractBinding(req);
            return $$"""
            {
              "schema": "theme_analysis_v1",
              "request": "{{binding}}",
              "interests": []
            }
            """;
        });

        var route = ModelRouteKey.From(new ModelConnectionSettings("deepseek", "https://api.deepseek.com/v1", "deepseek-flash", maxOutputTokens: 500));
        // The fixture crosses the token budget until earlier text is shortened.
        var dynamicMeter = new DynamicTokenMeter(scalars => scalars > 6000 ? 6000 : 3000);
        // InputLimit is 4000 (so 6000 exceeds, but 3000 fits)
        var model = new UnderstandingReviewModel(mockProvider, route, dynamicMeter, requestLimits: new(4500, 500));

        var input = new ReviewModelInput("op-compress", _scope, originals, [], _ => { });
        var result = await model.ReviewAsync(input);

        Assert.NotNull(result);
        var request = Assert.Single(mockProvider.Requests);
        using var doc = JsonDocument.Parse(request.ModelMessages[^1].Content);
        var sourcesArray = doc.RootElement.GetProperty("sources").EnumerateArray().ToList();

        // Excerpts keep the original aliases. Corrections and recent originals stay intact.
        Assert.Equal(10, sourcesArray.Count);
        var firstSource = sourcesArray[0];
        Assert.True(firstSource.GetProperty("isSummary").GetBoolean());
        Assert.StartsWith("Conversation message content item number 1", firstSource.GetProperty("text").GetString()!);
        Assert.False(result.SuppliedCoverage[0].Complete);
        Assert.Equal(originals[0].Source, result.SuppliedCoverage[0].Source);

        // Key correction should be preserved in full text
        Assert.Contains(sourcesArray, s => s.GetProperty("text").GetString()!.Contains("更正：前面提到的参数不对，换成0.75"));

        // Recent items preserved
        Assert.Contains(sourcesArray, s => s.GetProperty("text").GetString()!.Contains("number 10"));
    }

    [Fact]
    public async Task OverCapacity_StillOverAfterCompression_FailsWithCapacityExceeded()
    {
        var originals = new List<ReviewOriginal>();
        for (var i = 1; i <= 8; i++)
        {
            originals.Add(CreateOriginal(i, $"Message {i} with substantial content"));
        }

        var mockProvider = new ReviewChatProvider(_ => "{}");
        var route = ModelRouteKey.From(new ModelConnectionSettings("deepseek", "https://api.deepseek.com/v1", "deepseek-flash", maxOutputTokens: 200));
        // Meter always reports 10,000 tokens, far above limit of 1,000
        var alwaysOverMeter = new FixedTokenMeter(10000);
        var model = new UnderstandingReviewModel(mockProvider, route, alwaysOverMeter, requestLimits: new(1200, 200));

        var input = new ReviewModelInput("op-fail", _scope, originals, [], _ => { });
        var ex = await Assert.ThrowsAsync<AgentProtocolException>(() => model.ReviewAsync(input));
        Assert.Contains("UNDERSTANDING_CAPACITY_EXCEEDED", ex.Message);
    }

    [Fact]
    public void ThemeAnalysisProtocol_StrictlyRejectsMissingOrInvalidSources()
    {
        var originals = new[]
        {
            CreateOriginal(1, "User intro"),
            CreateOriginal(2, "Assistant reply"),
            CreateOriginal(3, "User second query")
        };
        var binding = "test-binding-hash";

        // Case A: Missing sources property -> rejected
        var jsonMissingSources = $$"""
        {
          "schema": "theme_analysis_v1",
          "request": "{{binding}}",
          "interests": [
            { "topic": "Valid topic but missing sources" }
          ]
        }
        """;
        var resA = ThemeAnalysisProtocol.Parse(jsonMissingSources, binding, originals, _scope, "conv-1", DateTimeOffset.UtcNow);
        Assert.Empty(resA.Topics);

        // Case B: Empty sources array -> rejected
        var jsonEmptySources = $$"""
        {
          "schema": "theme_analysis_v1",
          "request": "{{binding}}",
          "interests": [
            { "topic": "Empty sources topic", "sources": [] }
          ]
        }
        """;
        var resB = ThemeAnalysisProtocol.Parse(jsonEmptySources, binding, originals, _scope, "conv-1", DateTimeOffset.UtcNow);
        Assert.Empty(resB.Topics);

        // Case C: Invalid source alias (s99 when only s1..s3 exist) -> rejected
        var jsonInvalidAlias = $$"""
        {
          "schema": "theme_analysis_v1",
          "request": "{{binding}}",
          "interests": [
            { "topic": "Invalid alias topic", "sources": ["s99"] }
          ]
        }
        """;
        var resC = ThemeAnalysisProtocol.Parse(jsonInvalidAlias, binding, originals, _scope, "conv-1", DateTimeOffset.UtcNow);
        Assert.Empty(resC.Topics);

        // Case D: Valid sources -> accepted
        var jsonValid = $$"""
        {
          "schema": "theme_analysis_v1",
          "request": "{{binding}}",
          "interests": [
            {
              "topic": "Valid theme topic",
              "attribution": "user",
              "tense": "current",
              "research_candidate": true,
              "sources": ["s1", "s3"]
            }
          ]
        }
        """;
        var resD = ThemeAnalysisProtocol.Parse(jsonValid, binding, originals, _scope, "conv-1", DateTimeOffset.UtcNow);
        var topic = Assert.Single(resD.Topics);
        Assert.Equal("Valid theme topic", topic.Topic);
        Assert.Equal(2, topic.Sources.Count);
    }

    [Fact]
    public async Task TopicMaterialPlugin_ShortTermTopicLifecycle_Match_RevisionIncrement_Expiration_Retraction()
    {
        var clock = new ManualClock();
        var materialStore = new MemoryTopicMaterialStore();
        var topicStore = new ConversationTopicStore(_db, clock);
        var reader = new DummyUnderstandingReader();
        var allowed = true;

        using var plugin = new TopicMaterialPlugin(
            materialStore,
            reader,
            _ => allowed,
            new DummyResolver(),
            clock,
            topicSource: topicStore);

        await plugin.StartAsync(default);

        var topicId = "topic-test-life";
        var topic = new ConversationTopic(
            topicId,
            _scope.ServantId,
            _scope.ProjectId,
            "conv-1",
            1,
            "Superconducting qubits",
            null,
            "user",
            "current",
            ["v1"],
            "User mentioned qubits",
            null,
            true,
            "technical topic",
            clock.Now.AddDays(2),
            clock.Now,
            TopicMaterialScope.Interest);

        topicStore.SaveTopics(_scope, "conv-1", [topic]);

        // Store material bound to Revision = 1
        var material = new TopicMaterial(
            "mat-1",
            topicId,
            1,
            TopicMaterialScope.Interest,
            "Superconducting circuits use Josephson junctions.",
            "Relevant to qubit hardware.",
            "Quantum Hardware Paper",
            "https://example.org/qubit",
            clock.Now,
            clock.Now.AddDays(2));
        materialStore.Add(_scope, material);

        var toolScope = new ToolScope("conv-1", _scope.ServantId, _scope.ProjectId);

        // 1. Matched and delivered
        var promptBlocks = plugin.BuildPrompt(toolScope, "Tell me about quantum computing", false);
        var block = Assert.Single(promptBlocks);
        Assert.Contains("Superconducting circuits use Josephson junctions", block.Text);

        // 2. Permission denied -> empty
        allowed = false;
        Assert.Empty(plugin.BuildPrompt(toolScope, "Tell me about quantum computing", false));
        allowed = true;

        // 3. Topic revision increments to 2 -> old material for revision 1 exits selection!
        var revisedTopic = topic with { Topic = "Topological qubits", Revision = 2 };
        topicStore.SaveTopics(_scope, "conv-1", [revisedTopic]);
        Assert.Empty(plugin.BuildPrompt(toolScope, "Tell me about quantum computing", false));

        // 4. Topic retracted -> empty
        topicStore.RetractTopic(_scope, "conv-1", topicId);
        Assert.Empty(plugin.BuildPrompt(toolScope, "Tell me about quantum computing", false));

        // 5. Topic expired -> empty
        var expiredTopic = topic with { Topic = "Expired topic", Revision = 1, ActiveUntil = clock.Now.AddSeconds(-1) };
        topicStore.SaveTopics(_scope, "conv-1", [expiredTopic]);
        Assert.Empty(plugin.BuildPrompt(toolScope, "Tell me about quantum computing", false));
    }

    [Fact]
    public async Task MaterialResearchActivity_ManualTrigger_ResearchesShortTermTopic_WritesBoundMaterial()
    {
        var clock = new ManualClock();
        var materialStore = new MemoryTopicMaterialStore();
        var topicStore = new ConversationTopicStore(_db, clock);
        var reader = new DummyUnderstandingReader();
        var host = new DummyHost { Allowed = true };
        var lifetime = new DialogueContextLifetime();
        var searchSource = new DummySearchSource();
        var researchModel = new DummyResearchModel();

        var activity = new MaterialResearchActivity(
            host,
            reader,
            materialStore,
            researchModel,
            searchSource,
            lifetime,
            clock,
            topicSource: topicStore);

        var topicId = "topic-research-1";
        var topic = new ConversationTopic(
            topicId,
            _scope.ServantId,
            _scope.ProjectId,
            "conv-1",
            1,
            "Topological quantum computation",
            "Majorana zero modes for fault-tolerant qubits",
            "user",
            "current",
            ["v1"],
            "User requested topological details",
            null,
            true,
            "research topic",
            clock.Now.AddDays(2),
            clock.Now,
            TopicMaterialScope.Interest);

        topicStore.SaveTopics(_scope, "conv-1", [topic]);

        // Run manual trigger on this specific topic
        await activity.RunManualAsync(_scope, topicId);

        // Verify search was invoked using the correction (since present) or topic
        Assert.Equal(1, searchSource.Calls);
        Assert.Contains("Majorana zero modes", searchSource.LastQuery);

        // Verify material store now contains material bound to topic and revision
        var activeMaterials = materialStore.GetActive(_scope);
        var mat = Assert.Single(activeMaterials);
        Assert.Equal(topicId, mat.TopicKey);
        Assert.Equal(1, mat.TopicRevision);
        Assert.Equal("https://example.org/majorana", mat.SourceUrl);
        Assert.Contains("Topological error protection", mat.Summary);
    }

    [Fact]
    public void Topic_store_rejects_cross_scope_writes_and_forgets_only_the_requested_role()
    {
        var store = new ConversationTopicStore(_db);
        var now = DateTimeOffset.UtcNow;
        var roleA = new ConversationScope("mash", "p1");
        var roleB = new ConversationScope("other", "p1");
        var topic = new ConversationTopic("scope-test", roleA.ServantId, roleA.ProjectId, "shared-conversation",
            1, "A bounded topic", null, "user", "current", ["v1"], "basis", null, true, null, now.AddDays(1), now);
        Assert.Throws<ArgumentException>(() => store.SaveTopics(roleB, topic.ConversationId, [topic]));
        Assert.Throws<ArgumentException>(() => store.SaveTopics(roleA, "different-conversation", [topic]));
        store.SaveTopics(roleA, topic.ConversationId, [topic]);
        store.SaveTopics(roleB, topic.ConversationId, [topic with { RoleId = roleB.ServantId }]);
        store.ForgetConversation(topic.ConversationId, roleA.ServantId);
        Assert.Empty(store.GetActiveTopics(roleA));
        Assert.Single(store.GetActiveTopics(roleB));
    }

    [Fact]
    public void ConversationTopicStore_SQLite_Migrations_And_DerivedData_Contract()
    {
        var store = new ConversationTopicStore(_db);
        var now = DateTimeOffset.UtcNow;

        var t1 = new ConversationTopic("t1", "mash", "p1", "conv-a", 1, "Topic 1", null, "user", "current", ["v1"], "b1", null, true, "r1", now.AddDays(1), now, TopicMaterialScope.Interest);
        var t2 = new ConversationTopic("t2", "mash", "p1", "conv-b", 1, "Topic 2", null, "user", "current", ["v2"], "b2", null, true, "r2", now.AddDays(1), now, TopicMaterialScope.Interest);

        store.SaveTopics(new ConversationScope("mash", "p1"), "conv-a", [t1]);
        store.SaveTopics(new ConversationScope("mash", "p1"), "conv-b", [t2]);

        var active = store.GetActiveTopics(new ConversationScope("mash", "p1"));
        Assert.Equal(2, active.Count);

        // Updating t1 auto-increments revision
        store.SaveTopics(new ConversationScope("mash", "p1"), "conv-a", [t1 with { Topic = "Topic 1 Updated" }]);
        var loaded = Assert.Single(store.GetActiveTopics(new ConversationScope("mash", "p1"), "conv-a"));
        Assert.Equal(2, loaded.Revision);
        Assert.Equal("Topic 1 Updated", loaded.Topic);

        // Test ForgetConversation deletes only conv-a
        store.ForgetConversation("conv-a", "mash");
        var afterForget = store.GetActiveTopics(new ConversationScope("mash", "p1"));
        Assert.Single(afterForget);
        Assert.Equal("t2", afterForget[0].Id);

        // Test Export
        var exported = store.Export();
        Assert.NotNull(exported.ToString());
        Assert.Contains("t2", exported.ToString());

        // Test ForgetAll clears all
        store.ForgetAll();
        Assert.Empty(store.GetActiveTopics(new ConversationScope("mash", "p1")));
    }

    private static ReviewOriginal CreateOriginal(int index, string text)
    {
        var scope = new ConversationScope("mash", "test-project");
        var snapshot = new ConversationSourceSnapshot(
            new("conv-1", scope.ServantId, scope.ProjectId),
            "m" + index,
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("msg-" + index))).ToLowerInvariant(),
            new DateTimeOffset(2026, 11, 6, 10, 0, 0, TimeSpan.Zero).AddMinutes(index),
            index % 2 == 0 ? ConversationSourceKind.Assistant : ConversationSourceKind.User,
            "test-project",
            index);
        var runes = text.EnumerateRunes().Count();
        var coverage = new UnderstandingSourceCoverage(snapshot, 0, runes, runes);
        return new ReviewOriginal(snapshot, text, coverage);
    }

    private static string ExtractBinding(ChatRequest request)
    {
        using var json = JsonDocument.Parse(request.ModelMessages[^1].Content);
        return json.RootElement.GetProperty("request").GetString()!;
    }

    #region Test Doubles

    private sealed class ReviewModelFactory(IReviewModel model) : IReviewModelFactory
    {
        public bool Available(ConversationScope scope) => true;
        public IReviewModel Create(ConversationScope scope, UnderstandingState state, IChatDispatchGate admission) => model;
    }

    private sealed class PagedTimeline : IExperienceTimeline, IExperienceSourceReader
    {
        public readonly List<ExperienceSourceHead> Heads = [];
        private readonly List<string> _texts = [];

        public void AddUserMessage(string text) => AddMessage(ConversationSourceKind.User, text);
        public void AddAssistantMessage(string text) => AddMessage(ConversationSourceKind.Assistant, text);

        private void AddMessage(ConversationSourceKind kind, string text)
        {
            var n = Heads.Count + 1;
            var scope = new ConversationScope("mash", "test-project");
            var snapshot = new ConversationSourceSnapshot(
                new("conv-1", scope.ServantId, scope.ProjectId),
                "m" + n,
                Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("msg-" + n + ":" + text))).ToLowerInvariant(),
                new DateTimeOffset(2026, 11, 6, 10, 0, 0, TimeSpan.Zero).AddMinutes(n),
                kind,
                "test-project",
                n);
            var runes = text.EnumerateRunes().Count();
            Heads.Add(new(new("conv-1", snapshot.MessageId, n, snapshot.OccurredAtUtc), snapshot, runes));
            _texts.Add(text);
        }

        public long Boundary(ConversationScope scope) => Heads.Count;
        public long InputRevision(ConversationScope scope) => Heads.Count;

        public ExperienceScanPage ScanHeads(ConversationScope scope, ExperienceScanCursor cursor, int maxItems = 32, CancellationToken token = default)
        {
            var items = Heads.Where(h => h.Source.SourceRevision > cursor.Sequence).Take(maxItems).ToArray();
            return new(ExperienceSourceStatus.Available, items,
                items.Length == 0 ? cursor : new(items[^1].Source.SourceRevision, items[^1].Source.MessageId),
                Heads.Any(h => h.Source.SourceRevision > (items.LastOrDefault()?.Source.SourceRevision ?? cursor.Sequence)));
        }

        public ExperienceSourceRead Read(ConversationScope scope, HistoryAnchor anchor, CancellationToken token = default)
        {
            var idx = Heads.FindIndex(h => h.Anchor == anchor);
            if (idx < 0) return new(ExperienceSourceStatus.Unavailable, null);
            var head = Heads[idx];
            var text = _texts[idx];
            return new(ExperienceSourceStatus.Available, new(scope, anchor, head.Source.Kind, text, head.Source.Fingerprint));
        }

        public ConversationSourceSnapshot Read(ToolScope scope, string id, ConversationSourceKind kind)
            => Heads.Single(h => h.Source.MessageId == id).Source;

        public bool IsCurrent(ConversationSourceSnapshot source)
            => Heads.Any(h => h.Source.MessageId == source.MessageId && h.Source.Fingerprint == source.Fingerprint);

        public ExperienceWindowPage ReadPage(ConversationScope scope, string conversationId, int startSequence = 1, ExperienceWindowCursor? cursor = null, int maxItems = 8, int maxScalars = 36000, CancellationToken token = default)
        {
            var fromSeq = cursor?.StartSequence ?? (startSequence - 1);
            var items = new List<ExperienceWindowItem>();
            var filteredHeads = Heads.Where(h => h.Source.SourceRevision > fromSeq).Take(maxItems).ToList();
            foreach (var head in filteredHeads)
            {
                var idx = Heads.IndexOf(head);
                var text = _texts[idx];
                items.Add(new ExperienceWindowItem(head.Anchor, head.Source, text, head.TotalScalars, head.TotalScalars, true, head.Source.SourceRevision));
            }
            var throughSeq = items.Count > 0 ? items[^1].Anchor.Sequence : fromSeq;
            var hasMore = Heads.Any(h => h.Source.SourceRevision > throughSeq);

            return new ExperienceWindowPage(ExperienceSourceStatus.Available, items, null, "v1", throughSeq, hasMore);
        }

        public ExperienceWindowPage ReadAdjacent(ConversationScope scope, HistoryAnchor anchor, CancellationToken token = default)
            => throw new NotSupportedException();
    }

    private sealed class ReviewChatProvider : IChatProvider
    {
        private readonly Func<ChatRequest, string> _responder;
        public readonly List<ChatRequest> Requests = [];

        public ReviewChatProvider(Func<ChatRequest, string> responder) => _responder = responder;
        public string ProviderId => "deepseek";
        public string ModelId => "deepseek-flash";

        public Task<IReadOnlyList<ProviderModel>> ListModelsAsync(CancellationToken token) => Task.FromResult<IReadOnlyList<ProviderModel>>([]);

        public async IAsyncEnumerable<ChatStreamChunk> StreamAsync(ChatRequest request, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            var content = _responder(request);
            yield return new ChatStreamChunk(content, IsComplete: true, FinishReason: "stop", Usage: new ChatUsage(100, 50));
            await Task.CompletedTask;
        }

        public Task<ChatUsage?> MeasureAsync(ChatRequest request, CancellationToken cancellationToken = default)
            => Task.FromResult<ChatUsage?>(new ChatUsage(100, 50));

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class DynamicTokenMeter : IRequestTokenMeter
    {
        private readonly Func<int, int> _meter;
        public DynamicTokenMeter(Func<int, int> meter) => _meter = meter;
        public TokenMeasurement Measure(ModelRouteKey route, ChatRequest request)
        {
            using var doc = JsonDocument.Parse(request.ModelMessages[^1].Content);
            var count = doc.RootElement.GetProperty("sources").EnumerateArray().Sum(s => s.GetProperty("includedScalars").GetInt32());
            return new TokenMeasurement(_meter(count), TokenCountKind.Estimated, "synthetic");
        }
        public void RecordUsage(ModelRouteKey route, ChatRequest request, ChatUsage usage) { }
        public void Invalidate(ModelRouteKey route) { }
    }

    private sealed class FixedTokenMeter : IRequestTokenMeter
    {
        private readonly int _tokens;
        public FixedTokenMeter(int tokens) => _tokens = tokens;
        public TokenMeasurement Measure(ModelRouteKey route, ChatRequest request) => new TokenMeasurement(_tokens, TokenCountKind.Estimated, "synthetic");
        public void RecordUsage(ModelRouteKey route, ChatRequest request, ChatUsage usage) { }
        public void Invalidate(ModelRouteKey route) { }
    }

    private sealed class ManualClock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 11, 6, 12, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class MemoryTopicMaterialStore : ITopicMaterialStore
    {
        private readonly List<TopicMaterial> _items = [];
        public void Add(ConversationScope scope, TopicMaterial material) => _items.Add(material);
        public IReadOnlyList<TopicMaterial> GetActive(ConversationScope scope) => _items.ToArray();
        public bool CanResearch(ConversationScope scope, string topicKey, DateTimeOffset now, TimeSpan cooldown, int dailyLimit) => true;
        public bool TryReserveResearch(ConversationScope scope, string topicKey, DateTimeOffset now, TimeSpan refreshAfter, int dailyLimit = 6) => true;
        public void MarkUsed(ConversationScope scope, string materialId, DateTimeOffset usedAt) { }
        public void DeleteScope(ConversationScope scope) => _items.Clear();
        public void RecordOffered(ConversationScope scope, string materialId, DateTimeOffset now) { }
        public void RecordUsed(ConversationScope scope, string materialId, DateTimeOffset now) { }
        public void PruneExpired(ConversationScope scope, DateTimeOffset now) => _items.RemoveAll(i => i.ExpiresAt <= now);
    }

    private sealed class DummyUnderstandingReader : IUnderstandingReader
    {
        public UnderstandingProjection Query(ConversationScope scope, string query, bool historical = false, string? entryId = null) => new(1, []);
    }

    private sealed class DummyResolver : IAgentQueryResolver
    {
        public ValueTask<string?> ResolveAsync(ToolScope scope, ProtectedQueryReference reference, CancellationToken token) => ValueTask.FromResult<string?>("resolved");
    }

    private sealed class DummyHost : IMaterialResearchHost
    {
        public bool Allowed { get; set; } = true;
        private readonly MaterialResearchConfiguration _config = new(
            new("conv-1", "mash", "test-project"),
            new ModelConnectionSettings("fixture", "https://example.org/v1", "model", contextWindowOverride: 32768),
            "grant-1");
        public MaterialResearchConfiguration? Capture() => Allowed ? _config : null;
        public bool TryCommit(MaterialResearchConfiguration config, Action action)
        {
            if (!Allowed || config != _config) return false;
            action();
            return true;
        }
    }

    private sealed class DummySearchSource : IMaterialResearchSource
    {
        public int Calls;
        public string? LastQuery;
        public Task<IReadOnlyList<MaterialSearchResult>> SearchAsync(ToolScope scope, string query, CancellationToken token, string? activityId = null)
        {
            Calls++;
            LastQuery = query;
            return Task.FromResult<IReadOnlyList<MaterialSearchResult>>([
                new("Majorana Zero Modes in Nanowires", "https://example.org/majorana", "Topological error protection demonstration.")
            ]);
        }
    }

    private sealed class DummyResearchModel : IMaterialResearchModel
    {
        public Task<string?> GenerateQueryAsync(MaterialResearchRequest r, IChatDispatchGate gate, Action<CancellationToken> current, CancellationToken token)
        {
            var chat = ChatRequest.CreateAgent(r.Scope.RoleId, r.Scope.ConversationId, [new(ModelMessageRole.System, "synthetic request", [])],
                metadata: new Dictionary<string, string> { ["fgo_auxiliary"] = "topic_material_research", ["fgo_activity_id"] = r.ActivityId }, maxOutputTokens: 384);
            gate.Dispatch(chat, ModelRouteKey.From(r.Connection), () => 0, token);
            return Task.FromResult<string?>("Majorana zero modes fault tolerant");
        }

        public Task<MaterialResearchDraft?> SummarizeAsync(MaterialResearchRequest r, IReadOnlyList<MaterialSearchResult> results, IChatDispatchGate gate, Action<CancellationToken> current, CancellationToken token)
        {
            var chat = ChatRequest.CreateAgent(r.Scope.RoleId, r.Scope.ConversationId, [new(ModelMessageRole.System, "synthetic request", [])],
                metadata: new Dictionary<string, string> { ["fgo_auxiliary"] = "topic_material_research", ["fgo_activity_id"] = r.ActivityId }, maxOutputTokens: 768);
            gate.Dispatch(chat, ModelRouteKey.From(r.Connection), () => 0, token);
            return Task.FromResult<MaterialResearchDraft?>(new MaterialResearchDraft(0, "Topological error protection via non-Abelian statistics.", "Direct experimental relevance."));
        }
    }

    #endregion
}
