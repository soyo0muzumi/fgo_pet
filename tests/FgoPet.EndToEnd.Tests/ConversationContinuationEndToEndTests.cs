using System.IO;
using FgoPet.App.Dialogue;
using FgoPet.App.Privacy;
using FgoPet.Core.Dialogue;
using FgoPet.Core.Packs;
using FgoPet.Core.Settings;
using FgoPet.Dialogue.Settings;
using FgoPet.Infrastructure.Dialogue;
using FgoPet.Infrastructure.Memory;
using FgoPet.Infrastructure.Packs;
using FgoPet.Infrastructure.Persistence;
using FgoPet.Infrastructure.Providers;
using Xunit;

namespace FgoPet.EndToEnd.Tests;

public sealed class ConversationContinuationEndToEndTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"fgo-continuation-{Guid.NewGuid():N}.db");
    private readonly RuntimeDatabase _database;
    private readonly SqliteConversationRepository _conversations;
    private readonly SqliteMemoryRepository _memories;
    private readonly DialogueContextLifetime _lifetime = new();
    private static readonly ContentContextKey Key = new("mash", "test", "1", "default", "1", "1");
    public ConversationContinuationEndToEndTests()
    {
        _database = new(_path, pooling: false);
        new RuntimeDatabaseMigrator(_database).Migrate();
        _conversations = new(_database); _memories = new(_database);
        Seed("old-a", "a", ["导师面谈先讲信号项目", "收到", "改为先讲无线实验", "已更新"]);
        Seed("old-b", "b", ["周五聚餐独有内容", "收到"]);
    }

    [Fact]
    public async Task New_chat_receives_anchored_original_correction_and_preserves_project_on_reopen()
    {
        var provider = new Provider();
        var orchestrator = Create(provider);
        var first = await orchestrator.SendAsync("mash", "继续昨天面谈准备", default, new("a", "项目 A"));
        Assert.Equal(ConversationSendStatus.Completed, first.Status);
        var actual = string.Join("\n", provider.Requests.Last().Messages.Select(message => message.Text));
        Assert.Contains("recalled:old-a:", actual);
        Assert.Contains("改为先讲无线实验", actual);
        Assert.DoesNotContain("周五聚餐独有内容", actual);
        Assert.All(provider.Requests.Take(provider.Requests.Count - 1), request => Assert.Null(request.Tools));
        var other = await orchestrator.SendAsync("mash", "周五聚餐", default, new("b", "项目 B"));
        Assert.Equal(ConversationSendStatus.Completed, other.Status);
        Assert.NotEqual(first.ConversationId, other.ConversationId);
        var reopened = new SqliteConversationRepository(new RuntimeDatabase(_path, pooling: false));
        Assert.Equal("a", reopened.GetConversation(first.ConversationId, "mash")!.ProjectId);
        Assert.Equal("b", reopened.GetConversation(other.ConversationId, "mash")!.ProjectId);
        Assert.DoesNotContain("无线实验", string.Join("\n", provider.Requests.Last().Messages.Select(message => message.Text)));
    }

    [Fact]
    public async Task Deletion_during_delayed_recall_drains_old_request_before_removing_source()
    {
        var provider = new Provider { BlockRecall = true };
        var orchestrator = Create(provider);
        var sending = orchestrator.SendAsync("mash", "继续昨天面谈准备", default, new("a", "项目 A"));
        await provider.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var deletion = new UserDataDeletionService(_database, _conversations, _memories, dialogueLifetime: _lifetime)
            .DeleteConversationAsync("old-a", "mash", default);
        Assert.False(deletion.IsCompleted);
        provider.Release.TrySetResult();
        Assert.Equal(ConversationSendStatus.Cancelled, (await sending).Status);
        await deletion.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Null(_conversations.GetConversation("old-a", "mash"));
        Assert.Single(provider.Requests);
        Assert.Empty(new SqliteConversationRecallRepository(_database).Discover(new("mash", "a"), "", "无线实验"));
    }

    private ConversationOrchestrator Create(Provider provider)
    {
        var settings = new Settings();
        var resolver = new Resolver(provider);
        var contexts = new ModelContextResolver(_ => provider);
        var meter = new RequestTokenMeter();
        var recall = new ConversationRecallService(new SqliteConversationRecallRepository(_database), resolver, settings, contexts, meter);
        return new(resolver, new Content(), _conversations, _memories, new PromptComposer(meter), TimeProvider.System,
            settings: settings, contextResolver: contexts, tokenMeter: meter, lifetime: _lifetime, recall: recall);
    }
    private void Seed(string id, string project, string[] messages)
    {
        _conversations.CreateConversation(id, "mash", Key, DateTimeOffset.UtcNow, project, project);
        for (var i = 0; i < messages.Length; i++) _conversations.Append(new(id + "-" + i, id, "mash",
            i % 2 == 0 ? ChatMessageRole.User : ChatMessageRole.Assistant, messages[i], ChatMessageStatus.Completed,
            DateTimeOffset.UtcNow, Key, i + 1));
    }
    private sealed class Settings : IDialogueSettingsStore
    {
        public DialogueSettings Load() => DialogueSettings.Defaults with { ModelConnection = new("test", "https://fixture.test", "m",
            toolsSupported: false, contextWindowOverride: 32768) };
        public void Save(DialogueSettings settings) => throw new NotSupportedException();
    }
    private sealed class Resolver(Provider provider) : IChatProviderResolver { public IChatProvider Resolve() => provider; }
    private sealed class Content : IConversationContentResolver
    {
        public Task<ContentBinding> ResolveAsync(string servantId, CancellationToken cancellationToken) => Task.FromResult(
            new ContentBinding(Key, new PersonaBundle("mash", "test", "1", "1", "你好", []), [], [], new string('a', 64), new string('b', 64)));
    }
    private sealed class Provider : IChatProvider
    {
        public string ProviderId => "test";
        public string ModelId => "m";
        public List<ChatRequest> Requests { get; } = [];
        public bool BlockRecall { get; init; }
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<IReadOnlyList<ProviderModel>> ListModelsAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
        public async IAsyncEnumerable<ChatStreamChunk> StreamAsync(ChatRequest request,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
        {
            Requests.Add(request);
            var recall = request.Messages[0].Text.StartsWith("判断用户");
            if (recall && BlockRecall) { Started.TrySetResult(); await Release.Task; }
            yield return new(recall ? """{"queries":["面谈"],"selected":[],"ambiguous":false}""" : "好的",
                IsComplete: true, FinishReason: "stop");
        }
    }
    public void Dispose() { foreach (var suffix in new[] { "", "-wal", "-shm" }) File.Delete(_path + suffix); }
}
