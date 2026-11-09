using FgoPet.App.Dialogue;
using FgoPet.Core.Dialogue;
using FgoPet.Extensibility;
using Xunit;

namespace FgoPet.App.Tests.Dialogue;

public sealed partial class NativeConversationIntegrationTests
{
    [Fact]
    public async Task Native_recall_receives_only_the_last_three_prior_user_turns()
    {
        var recall = new RecordingRecall();
        var messages = new[] { "第一轮", "第二轮", "第三轮", "第四轮", "当前消息" };
        var provider = new ScriptedProvider(Enumerable.Repeat(ProviderTurn.Final("好的。"), messages.Length).ToArray());
        await using var host = await TestHost.CreateAsync(provider, new RecordingTool(ToolEffect.ReadOnly),
            new RecordingObserver(), recall: recall);

        foreach (var message in messages)
        {
            var result = await host.Orchestrator.SendAsync(RoleId, message, default, new(ProjectId));
            Assert.Equal(ConversationSendStatus.Completed, result.Status);
        }

        Assert.Equal(messages.Length, recall.ContextCalls.Count);
        Assert.Empty(recall.ContextCalls[0]);
        Assert.Equal(new[] { "第四轮", "第三轮", "第二轮" }, recall.ContextCalls[^1]);
        Assert.Empty(recall.LegacyCalls);
    }

    private sealed class RecordingRecall : IConversationRecall
    {
        public List<IReadOnlyList<string>> ContextCalls { get; } = [];
        public List<string> LegacyCalls { get; } = [];

        public Task<ConversationRecallResult> RetrieveAsync(ConversationScope scope, string currentConversationId,
            string userMessage, CancellationToken cancellationToken)
        {
            LegacyCalls.Add(userMessage);
            return Task.FromResult(new ConversationRecallResult(RecallStatus.Empty, []));
        }

        public Task<ConversationRecallResult> RetrieveAsync(ConversationScope scope, string currentConversationId,
            string userMessage, IReadOnlyList<string> recentTurns, CancellationToken cancellationToken)
        {
            ContextCalls.Add(recentTurns);
            return Task.FromResult(new ConversationRecallResult(RecallStatus.Empty, []));
        }
    }
}
