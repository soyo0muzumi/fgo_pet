using FgoPet.App.Dialogue;
using FgoPet.Core.Portraits;
using Xunit;

namespace FgoPet.App.Tests.Dialogue;

public sealed class StructuredOutputValidatorTests
{
    [Fact]
    public void Valid_json_maps_supported_fields_and_memory_candidate()
    {
        var output = StructuredOutputValidator.Validate(
            "{\"text\":\"你好，御主。\",\"emotion\":\"happy\",\"feedback_type\":\"encourage\",\"memory_candidate\":\"用户喜欢安静工作。\"}",
            ExpressionSemanticKeys.Core.ToHashSet(StringComparer.Ordinal));

        Assert.Equal("你好，御主。", output.Text);
        Assert.Equal(ExpressionSemantic.Happy, output.Expression);
        Assert.Equal("encourage", output.FeedbackType);
        Assert.Equal("用户喜欢安静工作。", output.MemoryCandidate!.Text);
    }

    [Fact]
    public void Unsupported_emotion_falls_back_to_neutral_and_malformed_json_keeps_safe_text()
    {
        var unsupported = StructuredOutputValidator.Validate(
            "{\"text\":\"收到。\",\"emotion\":\"laser\"}",
            ExpressionSemanticKeys.Core.ToHashSet(StringComparer.Ordinal));
        var malformed = StructuredOutputValidator.Validate(
            "{\"text\":\"仍然可以继续。\"",
            ExpressionSemanticKeys.Core.ToHashSet(StringComparer.Ordinal));

        Assert.Equal(ExpressionSemantic.Neutral, unsupported.Expression);
        Assert.Equal("仍然可以继续。", malformed.Text);
    }

    [Fact]
    public void Malformed_envelope_without_text_never_exposes_raw_json()
    {
        var output = StructuredOutputValidator.Validate(
            "{\"todos\":[{\"title\":\"secret\"}]",
            ExpressionSemanticKeys.Core.ToHashSet(StringComparer.Ordinal));

        Assert.Equal(StructuredOutputValidator.InvalidResponseMessage, output.Text);
        Assert.DoesNotContain("todos", output.Text, StringComparison.Ordinal);
    }
}
