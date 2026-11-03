using System.Text.Json;
using FgoPet.Core.Dialogue;
using FgoPet.Extensibility;
using FgoPet.Kernel.Agent;
using FgoPet.Understanding;
using FgoPet.Understanding.Contracts;
using Xunit;

namespace FgoPet.Plugin.Understanding.Tests;

public sealed class RecurrenceProtocolTests
{
    private static readonly ConversationSourceSnapshot Source = new(new("c", "mash", "p"), "m1", new('A',64),
        DateTimeOffset.UtcNow, ConversationSourceKind.Assistant, "Synthetic", 1);
    private static readonly ReviewModelInput Input = new("event", new("mash", "p"),
        [new(Source, "Actual observation", new(Source, 0, 18, 18))], [], _ => { });
    [Fact] public void A_quoted_observation_is_a_clue_only_and_unknown_or_duplicate_retrieval_aliases_are_rejected()
    {
        var response = JsonSerializer.Serialize(new { schema = 1, request = "binding", clues = new[] { new
        { theme = "comparison", summary = "Assistant compared actual reasons.", expressions = new[] { new { source = "s1", quote = "Actual" } } } }, retrieve = Array.Empty<string>() });
        var parsed = RecurrenceDiscoveryModel.Parse(response, "binding", Input, []);
        Assert.Single(parsed.Clues); Assert.Empty(parsed.RetrievedIds);
        Assert.Throws<AgentProtocolException>(() => RecurrenceDiscoveryModel.Parse(response, "wrong", Input, []));
        Assert.Throws<AgentProtocolException>(() => RecurrenceDiscoveryModel.Parse(response.Replace("\"retrieve\":[]", "\"retrieve\":[\"c1\"]"), "binding", Input, []));
    }
    [Theory]
    [InlineData("{\"schema\":1,\"request\":\"b\",\"clues\":[],\"retrieve\":[],\"Self\":\"Established\"}")]
    [InlineData("{\"schema\":1,\"request\":\"b\",\"clues\":[],\"retrieve\":[],\"retrieve\":[]}")]
    [InlineData("{\"schema\":1,\"request\":\"b\",\"clues\":[],\"retrieve\":[\"c1\",\"c2\",\"c3\",\"c4\"]}")]
    public void Clue_protocol_grants_no_Self_or_extra_retrieval_authority(string response) =>
        Assert.Throws<AgentProtocolException>(() => RecurrenceDiscoveryModel.Parse(response, "b", Input, []));
    [Theory] [InlineData(ConversationSourceKind.User, "Actual")] [InlineData(ConversationSourceKind.Assistant, "invented")]
    public void User_authorship_or_invented_quote_cannot_create_a_clue(ConversationSourceKind speaker, string quote)
    {
        var source = Source with { Kind = speaker };
        var input = Input with { Originals = [new(source, "Actual observation", new(source, 0, 18, 18))] };
        var response = JsonSerializer.Serialize(new { schema = 1, request = "b", clues = new[] { new
        { theme = "comparison", summary = "Assistant observed.", expressions = new[] { new { source = "s1", quote } } } }, retrieve = Array.Empty<string>() });
        Assert.Throws<AgentProtocolException>(() => RecurrenceDiscoveryModel.Parse(response, "b", input, []));
    }
}
