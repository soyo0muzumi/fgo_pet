using FgoPet.Core.Dialogue;
using FgoPet.Dialogue.Settings;
using FgoPet.Extensibility;
using FgoPet.Infrastructure.Dialogue;
using FgoPet.Kernel.Conversation;
using Microsoft.Extensions.Logging;

namespace FgoPet.App.Dialogue;

public sealed class NativeAgentAvailability
{
    private int ready;
    public bool IsReady => Volatile.Read(ref ready) != 0;
    internal void SetReady(bool value) => Volatile.Write(ref ready, value ? 1 : 0);
}

/// <summary>Recovery runs after global storage initialization. It never invokes model or tool ports.</summary>
public sealed class NativeAgentLifecyclePlugin(Func<SqliteAgentRunStore> runs,
    Func<IAgentFinalDeliveryStore> deliveries, Func<NativeConversationRuntime> runtime,
    Func<ConversationCapabilityRouter> capabilities, IConversationReader conversations,
    IDialogueSettingsStore settings, NativeAgentAvailability availability,
    ILogger<NativeAgentLifecyclePlugin>? logger = null) : IFgoPetPlugin, IApplicationReadyObserver, IDisposable
{
    private NativeConversationRuntime? owned;
    public PluginManifest Manifest { get; } = new("firstparty.native-agent", "1.0.0", 1, []);
    public PluginContributions Contributions => PluginContributions.Empty;
    public ValueTask StartAsync(CancellationToken stoppingToken) { stoppingToken.ThrowIfCancellationRequested(); return ValueTask.CompletedTask; }
    public async ValueTask OnApplicationReadyAsync(ApplicationReadiness readiness, CancellationToken token)
    {
        availability.SetReady(false);
        if (!readiness.StorageAvailable) return;
        await runs().CloseInterruptedRunsAsync(token);
        var owner = deliveries();
        for (var batch = 0; batch < 3; batch++)
        {
            var pending = owner.ReadPending(100);
            if (pending.Count == 0) break;
            foreach (var accepted in pending)
            {
                token.ThrowIfCancellationRequested();
                var scope = accepted.Identity.Scope;
                var published = owner.Publish(scope, accepted.DeliveryId);
                // The source can be archived/deleted between listing and publication. Keep its ledger and continue.
                if (published is null) continue;
                if (owner.TryClaimObservers(scope, accepted.DeliveryId))
                {
                    try
                    {
                        var root = conversations.LoadMessages(scope.ConversationId, scope.RoleId)
                            .SingleOrDefault(message => message.MessageId == accepted.Identity.RootUserMessageId);
                        if (root is not null)
                            capabilities().ObserveCompletedTurn(new(scope, root.MessageId, root.Text, published.Assistant.MessageId,
                                published.Assistant.Text, accepted.Output.SuggestedFact?.Text, settings.Load().ModelConnection, token));
                    }
                    catch (Exception) when (!token.IsCancellationRequested)
                    {
                        // An observer is best effort after the durable at-most-once claim; it cannot disable the host.
                        logger?.LogWarning("Native recovery observer dispatch failed: {ErrorCode}", "NATIVE_OBSERVER_DISPATCH_FAILED");
                    }
                }
            }
        }
        owned = runtime();
        availability.SetReady(true);
    }
    public async ValueTask StopAsync(CancellationToken token)
    {
        availability.SetReady(false);
        if (owned is not null) await owned.DisposeAsync();
    }
    public ValueTask DisposeAsync() => StopAsync(CancellationToken.None);
    public void Dispose()
    {
        availability.SetReady(false);
        owned?.Dispose();
    }
}
