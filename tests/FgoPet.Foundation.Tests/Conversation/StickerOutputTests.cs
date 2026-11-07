using System.Text.Json;
using FgoPet.App.Dialogue;
using FgoPet.Core.Portraits;
using Xunit;
using FgoPet.Kernel.Agent;
using FgoPet.Core.Dialogue;
using FgoPet.Extensibility;

namespace FgoPet.App.Tests.Dialogue;

public sealed class StickerOutputTests
{
    [Fact]
    public void A_collected_image_on_cooldown_is_not_replaced_by_a_different_meme()
    {
        var a = new StickerAsset("web." + new string('a', 48), "laugh", "轻笑", "", new string('A', 64), true);
        var b = new StickerAsset("web." + new string('b', 48), "laugh", "夸张大笑", "", new string('B', 64), true);
        var candidate = new StickerCandidate(a.Id, a.Intent, "enhance", "high", "playful", "after");
        var key = Enumerable.Range(0, 200).Select(i => i.ToString())
            .First(k => StickerPolicy.Select(candidate, [], k, "好笑。", catalog: [a, b]) is not null);
        Assert.Null(StickerPolicy.Select(candidate, [null, null, null, null, null, new(a.Id, a.Intent, "after")], key, "好笑。", catalog: [a, b]));
    }

    [Fact]
    public void Dynamic_nomination_is_syntactic_until_the_host_resolves_the_catalog()
    {
        var id = "web." + new string('a', 48);
        var raw = JsonSerializer.Serialize(new { text = "这个太好笑了。", sticker = new { assetId = id, intent = "playful", utility = "enhance", salience = "high", context = "playful", position = "after" } });
        var output = StructuredOutputValidator.ValidateFinalEnvelope(raw, ExpressionSemanticKeys.Core.ToHashSet(StringComparer.Ordinal), null);
        Assert.NotNull(output.Sticker);
        Assert.Null(StickerPolicy.Select(output.Sticker, [], "run", output.Text));
    }

    [Fact]
    public void Finalization_control_is_not_a_business_tool_but_unaccounted_calls_are_blocked()
    {
        var checkpoint = new AgentRunCheckpoint
        {
            Snapshot = new() { Identity = new("run", "user", new("chat", "role", null), "model", 1), ToolCalls = 1 },
            Calls = [new(new("finalize", "response.finalize", "{}"), AgentCallStatus.Completed)],
        };
        Assert.False(StickerPolicy.HasBusinessTools(checkpoint));
        Assert.True(StickerPolicy.HasBusinessTools(checkpoint with { Calls = [new(new("read", "fixture.read", "{}"))] }));
        Assert.True(StickerPolicy.HasBusinessTools(checkpoint with { Calls = [] }));
    }

    [Theory]
    [InlineData("null")]
    [InlineData("\"mash.happy\"")]
    [InlineData("{\"assetId\":\"../../private.png\"}")]
    [InlineData("{\"assetId\":\"mash.happy\",\"intent\":\"happy\",\"utility\":\"redundant\",\"salience\":\"high\",\"context\":\"social\",\"position\":\"after\"}")]
    [InlineData("{\"assetId\":\"mash.happy\",\"intent\":\"happy\",\"utility\":\"enhance\",\"salience\":\"high\",\"context\":\"correction\",\"position\":\"after\"}")]
    [InlineData("{\"assetId\":\"external.cry\",\"intent\":\"sad\",\"utility\":\"replace\",\"salience\":\"high\",\"context\":\"social\",\"position\":\"after\"}")]
    public void Bad_candidate_does_not_break_a_valid_reply(string candidate)
    {
        var output = StructuredOutputValidator.ValidateFinalEnvelope("{\"text\":\"仍然正常回复\",\"sticker\":" + candidate + "}",
            ExpressionSemanticKeys.Core.ToHashSet(StringComparer.Ordinal), null);
        Assert.Equal("仍然正常回复", output.Text);
        Assert.Null(output.Sticker);
    }

    [Fact]
    public void Selection_obeys_completed_reply_gaps_and_budgets()
    {
        var candidate = new StickerCandidate("mash.happy", "happy", "enhance", "high", "social", "after");
        var sent = new StickerSelection("mash.staring", "skeptical", "before");
        var key = Enumerable.Range(0, 100).Select(i => i.ToString()).First(k => StickerPolicy.Select(candidate, [], k, "太好了。") is not null);
        Assert.Null(StickerPolicy.Select(candidate, [null, null, sent], key, "太好了。"));
        Assert.NotNull(StickerPolicy.Select(candidate, [null, null, null, sent], key, "太好了。"));
        Assert.Null(StickerPolicy.Select(candidate, [null, null, null, null, new("mash.happy", "happy", "after")], key, "太好了。"));
        Assert.Null(StickerPolicy.Select(candidate, [null, null, null, null, null, new("mash.happy", "happy", "after")], key, "太好了。"));
        Assert.Null(StickerPolicy.Select(candidate, [null, null, null, sent, null, null, null, sent], key, "太好了。"));
        Assert.NotNull(StickerPolicy.Select(candidate, Enumerable.Repeat<StickerSelection?>(null, 10).Append(new("mash.happy", "happy", "after")).ToArray(), key, "太好了。"));
        Assert.Null(StickerPolicy.Select(candidate, [], key, "代码：```cs\nreturn;```"));
        Assert.Null(StickerPolicy.Select(candidate, [], key, "![信息图片](https://example.org/a.png)"));
        Assert.Null(StickerPolicy.Select(candidate, [], key, "结果已返回。", blocked: true));
        Assert.Equal(StickerPolicy.Select(candidate, [], key, "太好了。"), StickerPolicy.Select(candidate, [], key, "太好了。"));
    }

    [Fact]
    public void Catalog_has_thirty_unique_reviewed_assets_and_puzzled_is_not_refusal()
    {
        Assert.Equal(30, StickerCatalog.Assets.Count);
        Assert.Equal(30, StickerCatalog.Assets.Select(a => a.Id).Distinct().Count());
        Assert.Equal("confused", StickerCatalog.Find("mash.puzzled")!.Intent);
        Assert.All(StickerCatalog.Assets, a => Assert.Matches("^[A-F0-9]{64}$", a.Sha256));
    }

    [Fact]
    public void Final_preserves_optional_sticker_without_changing_text_or_emotion()
    {
        var output = StructuredOutputValidator.ValidateFinalEnvelope(
            """{"text":"做到了呢。","emotion":"happy","sticker":{"assetId":"mash.happy","intent":"happy","utility":"enhance","salience":"high","context":"social","position":"after"}}""",
            ExpressionSemanticKeys.Core.ToHashSet(StringComparer.Ordinal), null);
        Assert.Equal("做到了呢。", output.Text);
        Assert.Equal(ExpressionSemantic.Happy, output.Expression);
        Assert.Contains("mash.happy", JsonSerializer.Serialize(output));
    }
}
