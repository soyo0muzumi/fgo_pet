using System.Globalization;
using FgoPet.App.Feedback;
using FgoPet.Core.Events;
using FgoPet.Core.Focus;
using FgoPet.Core.Packs;
using FgoPet.Core.Portraits;
using Xunit;

namespace FgoPet.App.Tests.Feedback;

public sealed class EventFeedbackSelectorTests
{
    private static readonly CultureInfo Locale = CultureInfo.GetCultureInfo("zh-CN");

    private static DialogueBundle Bundle() => new(
        "zh-CN",
        new Dictionary<string, DialogueLocalization>(StringComparer.OrdinalIgnoreCase)
        {
            ["zh-CN"] = new("zh-CN", new Dictionary<string, IReadOnlyList<DialogueCandidate>>(StringComparer.Ordinal)
            {
                ["focus_completed"] = new DialogueCandidate[]
                {
                    new("focus_completed_01", "第一个完成文案", 50, ExpressionSemantic.Happy),
                    new("focus_completed_02", "第二个完成文案", 50, ExpressionSemantic.Excited),
                },
                ["focus_stopped"] = new DialogueCandidate[]
                {
                    new("focus_stopped_01", "停下了", 100, ExpressionSemantic.Concerned),
                },
            }),
        });

    private static RuntimeEvent CompletedEvent(int cycle = 1, string id = "event-focus-1") => new(
        id, "session-1", RuntimeEventType.FocusCompleted, DateTimeOffset.Parse("2026-08-27T09:25:00Z"),
        cycle, FocusPhase.Focus, "servant-mash", 1_500, 1_500, 2);

    [Fact]
    public void Select_prefers_the_exact_app_locale()
    {
        var selector = new EventFeedbackSelector();
        var result = selector.Select(CompletedEvent(), Bundle(), Locale);

        Assert.Equal("zh-CN", result.Locale);
        Assert.Contains("完成", result.Text);
        Assert.Equal(ExpressionSemantic.Happy, result.Expression);
    }

    [Fact]
    public void Select_falls_back_to_the_package_default_locale()
    {
        var selector = new EventFeedbackSelector();
        var result = selector.Select(CompletedEvent(), Bundle(), CultureInfo.GetCultureInfo("en-US"));

        Assert.Equal("zh-CN", result.Locale);
        Assert.NotEmpty(result.Text);
    }

    [Fact]
    public void Select_returns_neutral_fallback_when_no_bundle()
    {
        var selector = new EventFeedbackSelector();
        var result = selector.Select(CompletedEvent(), bundle: null, Locale);

        Assert.Equal(ExpressionSemantic.Neutral, result.Expression);
        Assert.NotEmpty(result.Text);
        Assert.DoesNotContain("servant", result.Text);
    }

    [Fact]
    public void Select_returns_neutral_fallback_for_an_unknown_event_type()
    {
        var selector = new EventFeedbackSelector();
        var unknown = CompletedEvent() with { Type = "unknown_event" };
        var result = selector.Select(unknown, Bundle(), Locale);

        Assert.Equal(ExpressionSemantic.Neutral, result.Expression);
    }

    [Fact]
    public void Consecutive_matching_events_choose_different_candidates()
    {
        var selector = new EventFeedbackSelector();
        var first = selector.Select(CompletedEvent(), Bundle(), Locale);
        var second = selector.Select(CompletedEvent(id: "event-focus-2"), Bundle(), Locale);

        Assert.NotEqual(first.CandidateId, second.CandidateId);
    }

    [Fact]
    public void Invalid_expression_values_map_to_default()
    {
        var bundle = new DialogueBundle("zh-CN", new Dictionary<string, DialogueLocalization>(StringComparer.OrdinalIgnoreCase)
        {
            ["zh-CN"] = new("zh-CN", new Dictionary<string, IReadOnlyList<DialogueCandidate>>(StringComparer.Ordinal)
            {
                ["focus_completed"] = new DialogueCandidate[]
                {
                    new("bad_expr_01", "文本", 100, Expression: null),
                },
            }),
        });

        var selector = new EventFeedbackSelector();
        var result = selector.Select(CompletedEvent(), bundle, Locale);
        Assert.Equal(ExpressionSemantic.Neutral, result.Expression);
    }

    [Fact]
    public void Neutral_fallbacks_cover_all_mapped_event_types_without_servant_names()
    {
        var selector = new EventFeedbackSelector();
        foreach (var type in new[]
                 {
                     RuntimeEventType.FocusStarted, RuntimeEventType.FocusCompleted, RuntimeEventType.FocusStopped,
                     RuntimeEventType.CycleCompleted, RuntimeEventType.BondLevelUp,
                 })
        {
            var result = selector.Select(CompletedEvent() with { Type = type }, bundle: null, Locale);
            Assert.NotEmpty(result.Text);
            Assert.Equal(ExpressionSemantic.Neutral, result.Expression);
            Assert.DoesNotContain("servant", result.Text.ToLowerInvariant());
        }
    }
}
