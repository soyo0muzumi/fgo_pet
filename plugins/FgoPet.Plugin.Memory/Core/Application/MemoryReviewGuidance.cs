using FgoPet.Extensibility;

namespace FgoPet.Plugin.Memory;

/// <summary>Provides fixed workflow guidance. The protected user text only selects whether it applies.</summary>
internal sealed class MemoryReviewGuidance(IAgentQueryResolver queries, Func<bool> available) : IAgentContextProvider
{
    internal const string Source = "memory-review-guidance";
    internal const string Instruction = "用户明确请求记住一件事。简短说明应用会尝试在回复后生成待审核的记忆候选，" +
        "可以到设置中的「关系与成长 → 她记得的」确认。候选是否生成以应用提示为准。" +
        "不要声称已经生成候选或保存记忆，也不要因为当前只有检索工具就断言这条信息只保留在本次对话。";

    private static readonly string[] RequestPrefixes =
    [
        "帮我记住", "请记住", "请帮我记住", "麻烦记住", "麻烦帮我记住", "能帮我记住", "可以帮我记住",
        "记住：", "记住:", "please remember ", "remember this:"
    ];

    public string Id => Source;
    public bool ProvidesTrustedInstructions => true;

    internal static bool Matches(string? userText)
    {
        if (userText is null) return false;
        var text = userText.TrimStart();
        return RequestPrefixes.Any(prefix => text.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
    }

    public async ValueTask<IReadOnlyList<AgentContextBlock>> BuildAsync(AgentContextRequest request, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (!available()) return [];
        var userText = await queries.ResolveAsync(request.Scope, request.Query, token).ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
        return available() && Matches(userText) ? [new(Source, AgentContextKind.Instruction, Instruction, 100)] : [];
    }
}
