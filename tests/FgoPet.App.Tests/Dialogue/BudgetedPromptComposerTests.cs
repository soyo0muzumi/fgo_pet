using FgoPet.App.Dialogue;
using FgoPet.Core.Dialogue;
using FgoPet.Core.Packs;
using Xunit;

namespace FgoPet.App.Tests.Dialogue;

public sealed class BudgetedPromptComposerTests
{
    [Fact]
    public void Memory_that_does_not_fit_is_omitted_as_a_whole_including_its_provenance()
    {
        var context = Context("继续");
        var memory = new FgoPet.Core.Memory.StoredMemory("large", "mash", new string('偏', 2000), true, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
        var prompt = new PromptComposer().Compose(new(context.ContentContext, context.Persona, [], [memory], "", [], "继续"), Route, Budget(8192));
        Assert.DoesNotContain(prompt.Messages, m => m.Text.Contains("source=\"memory:"));
        Assert.True(prompt.FitsBudget);
    }
    private static readonly ModelRouteKey Route = new("test", "endpoint", "m", "v1");
    private static PromptBudget Budget(int window) => PromptBudget.Resolve(
        new(Route, window, null, ContextLimitSource.Override, "fixture"), 2048);

    [Fact]
    public void Current_chinese_input_is_counted_and_not_silently_cut()
    {
        var input = new string('中', 6000);
        var result = new PromptComposer().Compose(Context(input), Route, Budget(8192));
        Assert.False(result.FitsBudget);
        Assert.Contains(input, result.Messages[^1].Text);
        Assert.True(result.Usage.InputTokens > 18000);
    }

    [Fact]
    public void Larger_model_accepts_the_same_complete_history()
    {
        var history = Enumerable.Range(0, 40).Select(i =>
            new PromptMessage(i % 2 == 0 ? ChatMessageRole.User : ChatMessageRole.Assistant,
                $"turn-{i:D2} " + new string('x', 500))).ToArray();
        var context = Context("继续", history);
        var small = new PromptComposer().Compose(context, Route, Budget(8192));
        var large = new PromptComposer().Compose(context, Route, Budget(131072));
        Assert.False(small.FitsBudget);
        Assert.True(large.FitsBudget);
        Assert.Equal(40, large.Messages.Count(message => message.Text.Contains("source=\"history:")));
        Assert.Contains(large.Messages, message => message.Text.Contains("turn-00"));
        Assert.Contains(large.Messages, message => message.Text.Contains("turn-39"));
    }

    [Fact]
    public void Tool_schema_and_pending_draft_share_the_actual_request_budget()
    {
        var draft = new string('草', 1200);
        var context = Context("修改步骤", draft: draft);
        var tools = new[] { new ChatToolDefinition("large_tool", "fixture",
            "{\"type\":\"object\",\"description\":\"" + new string('x', 10000) + "\"}") };
        var result = new PromptComposer().Compose(context, Route, Budget(8192), tools, "auto");
        Assert.False(result.FitsBudget);
        Assert.Contains(result.Messages, message => message.Text.Contains(draft));
        Assert.Equal(2048, result.MaxOutputTokens);
    }

    private static PromptContext Context(string user, PromptMessage[]? history = null, string? draft = null) =>
        new(new ContentContextKey("mash", "pack", "1", "default", "1", "1"),
            new PersonaBundle("mash", "pack", "1", "1", "认真回应。", []),
            [], [], "", history ?? [], user, pendingTodoDraft: draft);
}
