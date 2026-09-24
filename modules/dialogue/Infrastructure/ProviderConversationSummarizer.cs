using System.Text;
using FgoPet.Core.Dialogue;
using FgoPet.Infrastructure.Providers;

namespace FgoPet.Infrastructure.Dialogue;

public sealed class ProviderConversationSummarizer : IConversationSummarizer
{
    public async Task<SummaryAttempt> SummarizeAsync(IChatProvider provider, ChatRequest request, CancellationToken cancellationToken)
    {
        if (request.Tools is not null || request.MaxOutputTokens is not > 0)
            throw new ArgumentException("Summaries require a bounded request without tools.", nameof(request));
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
