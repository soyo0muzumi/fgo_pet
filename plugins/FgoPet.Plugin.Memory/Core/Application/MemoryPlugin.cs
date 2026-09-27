using FgoPet.Core.Memory;
using FgoPet.App.Dialogue;
using FgoPet.Extensibility;

namespace FgoPet.Plugin.Memory;

public sealed class MemoryPlugin(IMemoryRecall recall, IConversationSourceReader sources,
    IMemoryCandidateSink? candidates = null, MemoryExtractionQueue? extractions = null,
    Func<bool>? enabled = null) : IFgoPetPlugin, IConversationPromptProvider, IPostTurnObserver, IDisposable
{
    private int _closed;
    private bool _started;
    private CancellationToken _stopping;
    private bool Enabled => _started && !_stopping.IsCancellationRequested && Volatile.Read(ref _closed) == 0 && (enabled?.Invoke() ?? true);
    public PluginManifest Manifest { get; } = new("firstparty.memory", "1.0.0", 1, []);
    public PluginContributions Contributions => PluginContributions.Empty with { Prompts = [this], PostTurnObservers = [this] };
    public ValueTask StartAsync(CancellationToken stoppingToken)
    {
        stoppingToken.ThrowIfCancellationRequested();
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _closed) != 0, this);
        _stopping = stoppingToken;
        _started = true;
        return ValueTask.CompletedTask;
    }
    public IReadOnlyList<ConversationPromptBlock> BuildPrompt(ToolScope scope, string userMessage, bool toolsAvailable) =>
        Enabled ? BuildBlocks(recall.Query(new(scope.RoleId, scope.ProjectId), userMessage).Items, scope) : [];

    public static IReadOnlyList<ConversationPromptBlock> BuildBlocks(IEnumerable<StoredMemory> memories, ToolScope scope) =>
        memories.Where(memory => memory.IsEnabled && memory.ServantId == scope.RoleId &&
            (memory.ProjectId is null || memory.ProjectId == scope.ProjectId)).Select(memory =>
                new ConversationPromptBlock($"memory:{memory.MemoryId}", memory.Text + "\n适用范围：" +
                    (memory.ProjectId is null ? "当前角色通用" : "当前项目") +
                    (memory.Source is { } source
                        ? $"\n来源：{source.Kind}，{source.OccurredAtUtc:O}，conversation={source.ConversationId}，message={source.MessageId}。" +
                          (memory.SourceAvailable ? "已由用户审核确认。" : "来源记录已删除，保留已确认记忆。")
                        : "\n来源：旧版数据，来源信息不完整；已由用户审核确认。"))).ToArray();

    public void Observe(ConversationCompletedTurn turn)
    {
        if (!Enabled || candidates is null) return;
        turn.CancellationToken.ThrowIfCancellationRequested();
        var reader = new MemoryExtractionSourceReader(sources);
        if (turn.SuggestedFact is { } text)
        {
            var ticket = candidates.Begin(reader.Read(turn.Scope, turn.AssistantMessageId, MemoryEvidenceKind.AssistantSuggestion));
            try { candidates.Stage(ticket, [new(text)]); }
            finally { candidates.Abandon(ticket); }
        }
        if (Enabled && extractions is not null && turn.Connection is { } connection)
        {
            var ticket = candidates.Begin(reader.Read(turn.Scope, turn.UserMessageId, MemoryEvidenceKind.UserStatement));
            extractions.TryEnqueue(new(ticket, turn.UserText, turn.AssistantText, connection, turn.CancellationToken));
        }
    }
    public async ValueTask StopAsync(CancellationToken cancellationToken)
    {
        Interlocked.Exchange(ref _closed, 1);
        if (extractions is not null) await extractions.CancelAndDrainAsync(cancellationToken).ConfigureAwait(false);
    }
    public ValueTask DisposeAsync() => StopAsync(CancellationToken.None);
    public void Dispose() { Interlocked.Exchange(ref _closed, 1); extractions?.Dispose(); }
}
