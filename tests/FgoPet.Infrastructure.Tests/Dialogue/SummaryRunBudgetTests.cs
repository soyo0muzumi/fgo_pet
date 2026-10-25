using System.Runtime.CompilerServices;
using FgoPet.Core.Dialogue;
using FgoPet.Infrastructure.Dialogue;
using FgoPet.Kernel.Agent;
using Xunit;

namespace FgoPet.Infrastructure.Tests.Dialogue;

public sealed class SummaryRunBudgetTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Actual_summary_send_reserves_shared_budget_and_refusal_never_sends(bool deny)
    {
        var provider = new Provider(); var budget = new Budget(deny);
        var request = ChatRequest.CreateAgent("role", "conversation", [new(ModelMessageRole.User, "synthetic summary input", [])], maxOutputTokens: 128);
        IConversationSummarizer summarizer = new ProviderConversationSummarizer();
        if (deny) await Assert.ThrowsAsync<AgentBudgetExceededException>(() => summarizer.SummarizeAsync(provider, request, budget, default));
        else Assert.Equal("fixture summary", (await summarizer.SummarizeAsync(provider, request, budget, default)).Text);
        Assert.Equal(1, budget.Reservations); Assert.Equal(deny ? 0 : 1, provider.Sends);
    }
    private sealed class Budget(bool deny) : IModelRequestBudget
    {
        public int Reservations { get; private set; }
        public ValueTask ReserveAsync(CancellationToken token) { Reservations++; if (deny) throw new AgentBudgetExceededException("model_requests"); return ValueTask.CompletedTask; }
    }
    private sealed class Provider : IChatProvider
    {
        public string ProviderId => "fixture";
        public string ModelId => "model";
        public int Sends { get; private set; }
        public async IAsyncEnumerable<ChatStreamChunk> StreamAsync(ChatRequest request, [EnumeratorCancellation] CancellationToken token)
        { Sends++; await Task.Yield(); yield return new("fixture summary", true, "stop"); }
        public Task<IReadOnlyList<ProviderModel>> ListModelsAsync(CancellationToken token) => Task.FromResult<IReadOnlyList<ProviderModel>>([]);
    }
}
