using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;
using FgoPet.App.Dialogue;
using FgoPet.Core.Dialogue;
using FgoPet.Core.Packs;
using FgoPet.Extensibility;
using FgoPet.Infrastructure.Dialogue;
using FgoPet.Infrastructure.Persistence;
using FgoPet.Infrastructure.Providers;
using FgoPet.Kernel.Conversation;
using Xunit;

namespace FgoPet.EndToEnd.Tests;

public sealed class RepeatedCompactionRecoveryTests : IDisposable
{
    private readonly string path = Path.Combine(Path.GetTempPath(), $"fgo-repeated-compact-{Guid.NewGuid():N}.db");
    private static readonly ContentContextKey Key = new("mash", "test", "1", "default", "1", "1");
    private static readonly ConversationScope Scope = new("mash", "project");
    private static readonly ModelRouteKey Route = new("test", "endpoint", "model", "1");

    [Fact]
    public async Task Ten_compactions_and_reopens_preserve_originals_and_recover_facts_omitted_by_summary()
    {
        var database = new RuntimeDatabase(path, pooling: false);
        new RuntimeDatabaseMigrator(database).Migrate();
        var conversations = new SqliteConversationRepository(database);
        conversations.CreateConversation("c", "mash", Key, DateTimeOffset.UnixEpoch, "project", "fixture");
        var originals = new List<ChatMessage>();
        var provider = new OmissionProvider();
        var meter = new RequestTokenMeter();
        var budget = PromptBudget.Resolve(new(Route, 32768, null, ContextLimitSource.Override, "fixture"), 256);
        var sequence = 0;
        long previousProjection = 0;
        for (var cycle = 0; cycle < 10; cycle++)
        {
            // This is a deterministic recovery test, not an LLM quality benchmark.
            // The model deliberately omits early constraints, numbers and corrections.
            for (var index = 0; index < 8; index++)
            {
                sequence++;
                var fact = sequence switch
                {
                    1 => "限定：实验预算73，不允许发布；😀\n",
                    3 => "更正：实验预算改为91，仍不允许发布；\n",
                    _ => $"cycle-{cycle} source-{sequence} "
                };
                var message = new ChatMessage("m" + sequence, "c", "mash",
                    sequence % 2 == 1 ? ChatMessageRole.User : ChatMessageRole.Assistant,
                    fact + new string('x', 1800), ChatMessageStatus.Completed, DateTimeOffset.UnixEpoch.AddSeconds(sequence), Key, sequence);
                conversations.Append(message);
                originals.Add(message);
            }
            var store = new SqliteConversationContextStore(new RuntimeDatabase(path, pooling: false));
            var source = store.Read(Scope, "c");
            var before = Compose(source.Summary, source.UncoveredMessages, meter, budget);
            var service = new ConversationSummaryService(store, new ProviderConversationSummarizer(), meter, TimeProvider.System);
            using var lease = new DialogueContextLifetime().Acquire(default);
            Assert.True(await service.TryCompactAsync(source, provider, Route, budget, before.Usage.InputTokens,
                (summary, tail) => Compose(summary, tail, meter, budget), lease, () => { }, new CompactionCallBudget(), default));

            var reopened = new SqliteConversationContextStore(new RuntimeDatabase(path, pooling: false)).Read(Scope, "c");
            Assert.Equal(previousProjection + 1, reopened.ProjectionRevision);
            previousProjection = reopened.ProjectionRevision;
            Assert.NotNull(reopened.Summary);
            Assert.DoesNotContain("预算", reopened.Summary.SummaryText);
            Assert.Equal(originals.Where(m => m.Sequence > reopened.Summary.CoveredThroughSequence), reopened.UncoveredMessages);
            Assert.Equal(originals, conversations.LoadMessages("c", "mash"));
            var nextPrompt = Compose(reopened.Summary, reopened.UncoveredMessages, meter, budget);
            Assert.True(nextPrompt.FitsBudget);
            Assert.True(nextPrompt.Usage.InputTokens < before.Usage.InputTokens);

            var tools = NativeCompactedHistoryTools.Create(new SqliteCompactedHistoryRepository(new RuntimeDatabase(path, pooling: false)));
            var search = tools.Single(t => t.Descriptor.Name == "history.search");
            var hits = await search.InvokeAsync(new(new("c", "mash", "project"),
                JsonSerializer.SerializeToElement(new { query = "预算" })), default);
            Assert.True(hits.Success);
            Assert.Equal(new[] { "m1", "m3" }, hits.Payload.GetProperty("items").EnumerateArray()
                .Select(item => item.GetProperty("messageId").GetString()).OrderBy(id => id));

            var read = tools.Single(t => t.Descriptor.Name == "history.read");
            var recovered = await read.InvokeAsync(new(new("c", "mash", "project"),
                JsonSerializer.SerializeToElement(new { messageId = "m3", maxChars = 6000 })), default);
            Assert.True(recovered.Success);
            var item = Assert.Single(recovered.Payload.GetProperty("items").EnumerateArray());
            Assert.Equal(originals[2].Text, item.GetProperty("text").GetString());
            Assert.Equal("user", item.GetProperty("role").GetString());
            Assert.Equal("completed", item.GetProperty("status").GetString());
        }
        Assert.Equal(10, provider.Calls);
        Assert.Equal(9, provider.RequestsWithPreviousSummary);
    }

    private static ComposedPrompt Compose(ConversationSummary? summary, IReadOnlyList<ChatMessage> tail,
        IRequestTokenMeter meter, PromptBudget budget) => new PromptComposer(meter).Compose(
            new(Key, new PersonaBundle("mash", "test", "1", "1", "fixture", []), [], [], "",
                tail.Select(m => new PromptMessage(m.Role, m.Text)).ToArray(), "continue", conversationSummary: summary), Route, budget);

    private sealed class OmissionProvider : IChatProvider
    {
        public string ProviderId => "test";
        public string ModelId => "model";
        public int Calls { get; private set; }
        public int RequestsWithPreviousSummary { get; private set; }
        public Task<IReadOnlyList<ProviderModel>> ListModelsAsync(CancellationToken token) => throw new NotSupportedException();
        public async IAsyncEnumerable<ChatStreamChunk> StreamAsync(ChatRequest request,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            Calls++;
            if (request.Messages.Any(m => m.Text.Contains("source=\"previous_summary"))) RequestsWithPreviousSummary++;
            var last = request.Messages.SelectMany(m => Regex.Matches(m.Text, @"source:(m\d+):")
                .Select(match => match.Groups[1].Value)).Last();
            await Task.Yield();
            yield return new("任务目标：继续\n用户已确认的决定：未确定\n否定、更正及被替代事项：未确定\n尚未解决的问题：未确定\n下一步：继续\n来源消息：" + last,
                IsComplete: true, FinishReason: "stop");
        }
    }

    public void Dispose()
    {
        foreach (var suffix in new[] { "", "-wal", "-shm" }) File.Delete(path + suffix);
    }
}
