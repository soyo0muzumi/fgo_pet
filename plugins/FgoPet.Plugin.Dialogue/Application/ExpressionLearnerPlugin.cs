using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FgoPet.Core.Dialogue;
using FgoPet.Extensibility;
using Microsoft.Data.Sqlite;

namespace FgoPet.App.Dialogue;

internal sealed record ExpressionCollectionDiagnostic(DateTimeOffset AtUtc, string Correlation, string Stage, string Result);

internal sealed class ExpressionLearnerPlugin(ExpressionCandidateInbox inbox, IConversationReader conversations,
    IConversationSourceReader sources, Action<ExpressionCollectionDiagnostic>? diagnostics = null)
    : IFgoPetPlugin, IPostTurnObserver, IConversationContinuationProvider
{
    private CancellationToken stopping;
    private bool started;
    public PluginManifest Manifest { get; } = new("firstparty.expression-inbox", "0.2.0", 1, []);
    public PluginContributions Contributions => PluginContributions.Empty with { PostTurnObservers = [this], Continuations = [this] };
    public ValueTask StartAsync(CancellationToken token) { token.ThrowIfCancellationRequested(); stopping = token; started = true; return ValueTask.CompletedTask; }
    public ValueTask StopAsync(CancellationToken token) { started = false; return ValueTask.CompletedTask; }
    public ValueTask DisposeAsync() => StopAsync(default);
    public ConversationContributionResult? TryHandleInput(ToolScope scope, string userMessage) => null;
    public string? GetPromptState(ToolScope scope) => null;
    public void ClearRole(string roleId) { } // Switching roles is not permission to delete retained candidates.
    public void RemoveConversation(ToolScope scope) => inbox.ForgetConversation(scope.ConversationId, scope.RoleId);
    public void Observe(ConversationCompletedTurn turn)
    {
        if (!started || stopping.IsCancellationRequested || turn.CancellationToken.IsCancellationRequested || turn.Scope.RoleId != "mash_kyrielight") return;
        void Log(string stage, string result) => diagnostics?.Invoke(new(DateTimeOffset.UtcNow,
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(turn.UserMessageId)))[..12], stage, result));
        try
        {
            Log("received", "pending");
            var source = sources.Read(turn.Scope, turn.UserMessageId, ConversationSourceKind.User);
            var original = conversations.LoadMessages(turn.Scope.ConversationId, turn.Scope.RoleId)
                .Single(m => m.MessageId == turn.UserMessageId && m.Role == ChatMessageRole.User && m.Status == ChatMessageStatus.Completed);
            if (!sources.IsCurrent(source) || turn.CancellationToken.IsCancellationRequested) return;
            Log("source_validated", "user_original");
            Log("inbox_result", inbox.Collect(source, original.Text));
        }
        catch (Exception error) when (error is SqliteException or JsonException or InvalidOperationException)
        { Log("inbox_result", "source_or_store_unavailable"); }
    }
}
