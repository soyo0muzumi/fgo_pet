using System.IO;
using System.Net.Http;
using System.Net.Sockets;
using System.Text.Json;
using FgoPet.App.Dialogue;
using FgoPet.App.Memory;
using FgoPet.App.Providers;
using FgoPet.Core.Dialogue;
using FgoPet.Core.Memory;
using FgoPet.Core.Settings;
using FgoPet.Dialogue.Settings;
using FgoPet.Infrastructure.Dialogue;
using FgoPet.Infrastructure.Memory;
using FgoPet.Infrastructure.Persistence;
using FgoPet.Infrastructure.Providers;
using FgoPet.Infrastructure.Secrets;
using FgoPet.Memory.Settings;
using Xunit;

namespace FgoPet.EndToEnd.Tests;

public sealed partial class MemoryLifecycleEndToEndTests
{
    private sealed class LiveMemoryFactAttribute : FactAttribute
    {
        public LiveMemoryFactAttribute()
        {
            if (Environment.GetEnvironmentVariable("FGO_PET_MEMORY_LIVE") != "1")
                Skip = "Explicit opt-in to the configured local model gateway is required.";
        }
    }

    [LiveMemoryFact]
    public async Task Live_model_accumulates_and_uses_synthetic_project_history()
    {
        var connection = ReadLiveRoute();
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(8));
        using (var probe = new TcpClient())
        {
            try { await probe.ConnectAsync("127.0.0.1", 8326, deadline.Token); }
            catch (SocketException) { Assert.Fail("MEMORY_LIVE_GATEWAY_UNAVAILABLE"); }
        }

        using var http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false })
        { Timeout = TimeSpan.FromSeconds(90) };
        var factory = new ChatProviderFactory(new ProviderCatalog(), new WindowsCredentialStore(), http);
        var live = factory.Create(connection);
        var settings = new LiveSettings(connection);
        var clock = new SampleClock();
        var path = Path.Combine(Path.GetTempPath(), "memory-live-synthetic-" + Guid.NewGuid().ToString("N") + ".db");
        var database = new RuntimeDatabase(path, pooling: false);
        try
        {
            new RuntimeDatabaseMigrator(database).Migrate();
            var conversations = new SqliteConversationRepository(database);
            var sourceReader = new ConversationSourceReader(conversations);
            var repository = new SqliteMemoryRepository(database, settings, sourceReader);
            repository.StartSession();
            var recall = new MemoryRecallService(repository);
            var sink = new ObservingSink(repository)
            {
                Observed = (proposals, result) => Console.WriteLine("MEMORY_LIVE_WRITE " + JsonSerializer.Serialize(new
                {
                    status = result.Status.ToString(),
                    candidates = result.CandidateIds.Count,
                    proposals = proposals.Select(p => new
                    {
                        operation = p.Lifecycle?.Operation.ToString(), type = p.Lifecycle?.ContentType.ToString(),
                        evidenceCount = p.Lifecycle?.Evidence.Count, verified = p.Lifecycle?.SupportsAutomaticWrite,
                    }),
                })),
            };
            var lifetime = new DialogueContextLifetime();
            var meter = new RequestTokenMeter();
            var limits = new ModelContextResolver(_ => live, clock);
            await using var queue = new MemoryExtractionQueue(
                new ProviderMemoryCandidateExtractor(_ => live, limits, meter, recall, conversations), sink, repository,
                lifetime, captured => captured == connection, isSourceCurrent: new MemoryExtractionSourceReader(sourceReader).IsCurrent);
            var chat = new ConversationOrchestrator(new LiveResolver(live), new Content(), conversations, new(meter), clock,
                settings: settings, contextResolver: limits, tokenMeter: meter, lifetime: lifetime,
                capabilities: FgoPet.Testing.ActivatedCapabilities.Create(
                    new FgoPet.Plugin.Memory.MemoryPlugin(recall, sourceReader, sink, queue)));
            var project = new ConversationRequestContext("synthetic-moon-lamp", "月灯项目");
            var scope = new MemoryScope("mash", project.ProjectId);
            var issues = new List<string>();

            async Task<string> Send(string text, bool newConversation = false)
            {
                if (newConversation) chat.StartNewConversation("mash");
                var result = await chat.SendAsync("mash", text, deadline.Token, project);
                Assert.Equal(ConversationSendStatus.Completed, result.Status);
                _ = await sink.Completed.Reader.ReadAsync(deadline.Token);
                return conversations.LoadMessages(result.ConversationId, "mash")
                    .Single(m => m.MessageId == result.AssistantMessageId).Text;
            }

            _ = await Send("我正在修月灯项目的 PDF 导出，点导出就卡住了。");
            _ = await Send("总算搞定了！");
            var before = repository.ListMemories("mash");
            if (!before.Any(m => DescribesCompletedExport(m.Text)))
                issues.Add("missed_elliptical_completion");

            clock.Now += TimeSpan.FromDays(3);
            var reply = await Send("我又回到月灯项目了。你记得上次 PDF 导出是什么进展吗？", newConversation: true);
            if (!DescribesCompletedExport(reply))
                issues.Add("missed_cross_session_recall");
            var beforeRegression = repository.ListMemories("mash");
            if (beforeRegression.Count != before.Count)
                issues.Add("false_write_from_recall_question");
            _ = await Send("刚刚把月灯项目重新部署了一次，PDF 导出又卡住了。");
            var changed = repository.ListMemories("mash");
            if (!changed.Any(m => m.Relation == MemoryOperation.Transition &&
                beforeRegression.All(old => old.MemoryId != m.MemoryId)) || !changed.Any(m => m.HasNewerState))
                issues.Add("missed_state_transition");

            _ = await Send("确认一下，重部署之后这个 PDF 导出还是卡住的。");
            if (repository.ListMemories("mash").Count != changed.Count)
                issues.Add("duplicate_record");
            _ = await Send("假设我今天发布了月灯项目，其实只是想讨论发布会怎么办。测试病历号是 SYNTHETIC-743。");
            if (repository.ListMemories("mash").Count != changed.Count ||
                repository.ListMemories("mash").Any(m => m.Text.Contains("SYNTHETIC-743", StringComparison.Ordinal)))
                issues.Add("false_write_from_hypothesis_or_sensitive_input");

            _ = await Send("更正一下，我刚才说错了。卡住的是 CSV 导出，PDF 一直正常。");
            var correction = repository.ListCandidates("mash").LastOrDefault(c =>
                c.ReplacesMemoryId is not null && c.Status == MemoryCandidateStatus.Pending);
            if (correction is null)
                issues.Add("missed_correction_review");
            else
            {
                var corrected = repository.ReviewCandidate(correction.CandidateId, "mash", MemoryReviewAction.Approve, null, clock.GetUtcNow());
                if (corrected?.Verification != MemoryVerification.Confirmed || !corrected.Text.Contains("CSV"))
                    issues.Add("incorrect_reviewed_correction");
            }
            var oldCount = repository.ListMemories("mash").Count;
            settings.Save(new(true, false));
            repository.InvalidateWrites();
            _ = await Send("我完成了月灯的新文档校对。");
            if (repository.ListMemories("mash").Count != oldCount) issues.Add("write_after_revoke");
            if (!recall.QueryHistory(scope, "PDF 导出").Items.Any()) issues.Add("missing_historical_search");
            var automatic = repository.ListMemories("mash").FirstOrDefault(m => m.Verification == MemoryVerification.Recorded);
            if (automatic?.Source is { } source)
            {
                conversations.DeleteConversation(source.ConversationId, "mash");
                if (repository.ListMemories("mash").Any(m => m.MemoryId == automatic.MemoryId) ||
                    recall.QueryHistory(scope, automatic.MemoryId).Items.Any(m => m.MemoryId == automatic.MemoryId))
                    issues.Add("deleted_source_still_recallable");
            }
            Assert.True(issues.Count == 0, "MEMORY_LIVE_SAMPLE " + string.Join(",", issues));
        }
        finally
        {
            foreach (var suffix in new[] { "", "-wal", "-shm" }) File.Delete(path + suffix);
        }
    }

    private static bool DescribesCompletedExport(string text) =>
        text.Contains("导出") && new[] { "修复", "解决", "搞定", "修好", "恢复", "完成" }.Any(text.Contains) &&
        !new[] { "尚未修复", "还没搞定", "还没修好", "没解决" }.Any(text.Contains);

    private static ModelConnectionSettings ReadLiveRoute()
    {
        var state = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FgoPet", "settings.json");
        using var document = JsonDocument.Parse(File.ReadAllText(state));
        var connection = document.RootElement.GetProperty("model_connection");
        Assert.Equal("custom-openai-compatible", connection.GetProperty("provider_id").GetString());
        Assert.True(connection.GetProperty("base_url").GetString()?.TrimEnd('/') == "http://127.0.0.1:8326/v1",
            "MEMORY_LIVE_ROUTE_MISMATCH");
        Assert.True(connection.GetProperty("model_id").GetString() == "buddy/deepseek-v4.1-flash", "MEMORY_LIVE_MODEL_MISMATCH");
        return new("custom-openai-compatible", "http://127.0.0.1:8326/v1", "buddy/deepseek-v4.1-flash",
            toolsSupported: false, contextWindowOverride: 65536, maxOutputTokens: 4096);
    }

    private sealed class LiveResolver(IChatProvider provider) : IChatProviderResolver
    {
        public IChatProvider Resolve() => provider;
    }

    private sealed class LiveSettings(ModelConnectionSettings connection) : IMemorySettingsStore, IDialogueSettingsStore
    {
        private MemorySettings _memory = new(true, true);
        MemorySettings IMemorySettingsStore.Load() => _memory;
        public void Save(MemorySettings settings) => _memory = settings;
        DialogueSettings IDialogueSettingsStore.Load() => DialogueSettings.Defaults with { ModelConnection = connection };
        void IDialogueSettingsStore.Save(DialogueSettings settings) => throw new NotSupportedException();
    }

    private sealed class SampleClock : TimeProvider
    {
        public DateTimeOffset Now = new(2026, 11, 7, 9, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }
}
