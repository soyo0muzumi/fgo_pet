using System.IO;
using System.Text;
using FgoPet.Core.Dialogue;
using FgoPet.Infrastructure.Providers;

namespace FgoPet.Infrastructure.Dialogue;

public sealed class ProviderConversationSummarizer : IConversationSummarizer
{
    public Task<SummaryAttempt> SummarizeAsync(IChatProvider provider, ChatRequest request, CancellationToken cancellationToken)
        => SendAsync(provider, request, null, cancellationToken);
    public Task<SummaryAttempt> SummarizeAsync(IChatProvider provider, ChatRequest request,
        FgoPet.Kernel.Agent.IModelRequestBudget budget, CancellationToken cancellationToken)
        => SendAsync(provider, request, budget, cancellationToken);
    private static async Task<SummaryAttempt> SendAsync(IChatProvider provider, ChatRequest request,
        FgoPet.Kernel.Agent.IModelRequestBudget? budget, CancellationToken cancellationToken)
    {
        if (request.Tools is not null || request.MaxOutputTokens is not > 0)
            throw new ArgumentException("Summaries require a bounded request without tools.", nameof(request));
        cancellationToken.ThrowIfCancellationRequested();
        _ = ChatRequestInputEnvelope.Write(provider.ModelId, request);
        if (budget is not null) await budget.ReserveAsync(cancellationToken);
        var text = new StringBuilder();
        string? finish = null;
        ChatUsage? usage = null;
        var complete = false;
        await foreach (var chunk in provider.StreamAsync(request, cancellationToken))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (chunk.ToolCallDelta is not null || text.Length + chunk.TextDelta.Length > 6000)
                throw new ProviderRequestException(ProviderFailureCategory.InvalidResponse, "摘要内容不完整或超出限制。");
            text.Append(chunk.TextDelta);
            finish = chunk.FinishReason ?? finish;
            usage = chunk.Usage ?? usage;
            if (chunk.IsComplete) { complete = true; break; }
        }
        cancellationToken.ThrowIfCancellationRequested();
        return new(text.ToString(), complete ? finish : "incomplete", usage);
    }
}
