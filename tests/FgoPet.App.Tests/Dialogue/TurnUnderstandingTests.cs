using FgoPet.App.Dialogue;
using FgoPet.Core.Dialogue;
using FgoPet.Core.Packs;
using Xunit;

namespace FgoPet.App.Tests.Dialogue;

/// <summary>
/// The semantic reading reorders the same three control axes. These tests pin the deterministic
/// mapping and the fallback contract: an absent, unknown or malformed reading must reproduce the
/// previous lexical behaviour exactly.
/// </summary>
public sealed class TurnUnderstandingTests
{
    private static ResponseIntent Intent(string act = "continue", string followUp = "continue_thread")
        => new(act, "unspecified", "current_topic", "familiar", "as_needed", followUp, ["summary"]);

    private static PromptContext Context(ResponseIntent intent) => new(
        new("mash_kyrielight", "official.mash", "1", "casual", "1", "1"),
        new PersonaBundle("mash_kyrielight", "official.mash", "1", "1", "synthetic persona", []),
        [], [], "", [], "确实") { ResponseIntent = intent };

    private static (string Momentum, string Contribution, string Question) ControlOf(
        ResponseIntent intent, TurnUnderstanding? understanding)
    {
        var plan = MashExpressionV02.Select(Context(intent), understanding);
        return (plan.Control.Momentum.ToString(), plan.Control.Contribution.ToString(),
            plan.Control.QuestionPolicy.ToString());
    }

    [Fact]
    public void Backchannel_reading_with_no_information_delta_holds_and_reacts()
    {
        var reading = new TurnUnderstanding(TurnRelation.Respond, InformationDelta.None, TurnRequest.None);
        Assert.Equal(("Hold", "React", "None"), ControlOf(Intent(), reading));
    }

    [Fact]
    public void Content_bearing_reading_continues()
    {
        var reading = new TurnUnderstanding(TurnRelation.Extend, InformationDelta.New, TurnRequest.None);
        Assert.Equal(("Continue", "Observe", "None"), ControlOf(Intent(), reading));
    }

    [Fact]
    public void Redirect_reading_continues()
    {
        var reading = new TurnUnderstanding(TurnRelation.Redirect, InformationDelta.New, TurnRequest.None);
        Assert.Equal(("Continue", "Observe", "None"), ControlOf(Intent(), reading));
    }

    [Fact]
    public void Explicit_request_is_answered_without_being_compressed()
    {
        var reading = new TurnUnderstanding(TurnRelation.Request, InformationDelta.New, TurnRequest.NeedsAnswer);
        Assert.Equal(("Continue", "Observe", "None"), ControlOf(Intent(), reading));
    }

    [Fact]
    public void Close_reading_stops()
    {
        var reading = new TurnUnderstanding(TurnRelation.Close, InformationDelta.None, TurnRequest.None);
        Assert.Equal(("Stop", "React", "None"), ControlOf(Intent(), reading));
    }

    [Fact]
    public void Unknown_reading_reproduces_the_lexical_result()
    {
        var intent = Intent();
        Assert.Equal(ControlOf(intent, null), ControlOf(intent, TurnUnderstanding.Unknown));
    }

    [Fact]
    public void Semantic_reading_never_grants_a_question_when_the_intent_forbids_one()
    {
        var reading = new TurnUnderstanding(TurnRelation.Extend, InformationDelta.New, TurnRequest.None);
        var noQuestion = new ResponseIntent("continue", "unspecified", "current_topic", "familiar", "as_needed",
            "continue_thread", ["follow_up_question"]);
        Assert.Equal("None", ControlOf(noQuestion, reading).Question);
    }

    [Fact]
    public void Backchannel_reading_never_grants_a_question()
    {
        var reading = new TurnUnderstanding(TurnRelation.Respond, InformationDelta.None, TurnRequest.None);
        Assert.Equal("None", ControlOf(Intent(followUp: "optional_topic"), reading).Question);
    }

    [Fact]
    public void Well_formed_reading_parses()
    {
        var reading = TurnUnderstandingModel.Parse(
            """{"relation":"respond","information_delta":"none","request":"none"}""");
        Assert.Equal(TurnRelation.Respond, reading.Relation);
        Assert.Equal(InformationDelta.None, reading.InformationDelta);
        Assert.Equal(TurnRequest.None, reading.Request);
    }

    [Fact]
    public void Request_reading_parses()
    {
        var reading = TurnUnderstandingModel.Parse(
            """{"relation":"request","information_delta":"new","request":"needs_answer"}""");
        Assert.Equal(TurnRelation.Request, reading.Relation);
        Assert.Equal(TurnRequest.NeedsAnswer, reading.Request);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("{}")]
    [InlineData("""{"relation":"answer","information_delta":"none","request":"none"}""")]
    [InlineData("""{"relation":"respond","information_delta":"none"}""")]
    [InlineData("""{"relation":"respond","information_delta":"none","request":"none","extra":1}""")]
    public void Malformed_readings_degrade_to_unknown(string json)
        => Assert.True(TurnUnderstandingModel.Parse(json).IsUnknown);
}
