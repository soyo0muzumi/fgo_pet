using System.Collections.Immutable;
using System.Text.Json;
using FgoPet.Extensibility;
using FgoPet.Kernel.Agent;
using Xunit;

namespace FgoPet.Agent.Tests.Policy;

public sealed class InteractionBrokerTests
{
    private static readonly ToolScope Scope = new("conversation", "role", "project");
    private static readonly AgentRunIdentity Identity = new("run", "root", Scope, "model", 7);

    [Fact]
    public void Question_request_freezes_host_binding_and_has_independent_json_lifetime()
    {
        var clock = new ManualTime();
        var broker = new UserInputBroker(clock);
        UserInputRequest request;
        using (var document = JsonDocument.Parse("{\"questions\":[{\"id\":\"choice\",\"question\":\"选择方向\",\"options\":[\"甲\",\"乙\"]}]}"))
            request = broker.Create(Identity, 2, "call", 12, document.RootElement);
        Assert.Equal(Identity, request.Binding.Identity);
        Assert.Equal(2, request.Binding.StepNumber);
        Assert.Equal("call", request.Binding.CallId);
        Assert.Equal(12, request.Binding.WaitingRevision);
        Assert.True(Guid.TryParseExact(request.Binding.RequestId, "N", out _));
        Assert.Equal(clock.GetUtcNow().AddMinutes(15), request.Binding.ExpiresAt);
        var result = broker.Validate(request, Reply(request, new QuestionAnswer("choice", [1], "补充")), Scope);
        Assert.True(result.Success);
        Assert.Equal("乙", result.Payload.GetProperty("answers")[0].GetProperty("selectedOptions")[0].GetString());
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"questions\":[]}")]
    [InlineData("{\"questions\":[{\"id\":\"q\",\"question\":\"ok\",\"requestId\":\"model\"}]}")]
    [InlineData("{\"questions\":[{\"id\":\"q\",\"question\":\"ok\"}],\"scope\":\"model\"}")]
    [InlineData("{\"questions\":[{\"id\":\"Q\",\"question\":\"ok\"}]}")]
    [InlineData("{\"questions\":[{\"id\":\"q\",\"id\":\"q\",\"question\":\"ok\"}]}")]
    [InlineData("{\"questions\":[{\"id\":\"q\",\"question\":\"ok\",\"allowMultiple\":true}]}")]
    [InlineData("{\"questions\":[{\"id\":\"q\",\"question\":\"ok\",\"options\":[\"same\",\"same\"]}]}")]
    [InlineData("{\"questions\":[{\"id\":\"q\",\"question\":\"ok\"},{\"id\":\"q\",\"question\":\"again\"}]}")]
    public void Invalid_question_shape_is_rejected_with_safe_code(string json)
    {
        using var document = JsonDocument.Parse(json);
        var error = Assert.Throws<AgentStateException>(() => new UserInputBroker().Create(Identity, 1, "call", 1, document.RootElement));
        Assert.Equal("RUN_INVALID_QUESTIONS", error.Code);
    }

    [Theory]
    [InlineData(0, 1, 1, 1)]
    [InlineData(4, 1, 1, 1)]
    [InlineData(1, 9, 1, 1)]
    [InlineData(1, 1, 2001, 1)]
    [InlineData(1, 1, 1, 201)]
    public void Question_count_and_text_limits_are_enforced(int questions, int options, int textLength, int optionLength)
    {
        var args = JsonSerializer.SerializeToElement(new { questions = Enumerable.Range(0, questions).Select(i => new
        { id = "q" + i, question = new string('中', textLength), options = Enumerable.Range(0, options).Select(j => j + new string('文', optionLength - 1)) }) });
        Assert.Throws<AgentStateException>(() => new UserInputBroker().Create(Identity, 1, "call", 1, args));
    }

    [Fact]
    public void Exact_question_boundaries_and_multiselect_are_accepted()
    {
        var args = JsonSerializer.SerializeToElement(new { questions = Enumerable.Range(0, 3).Select(i => new
        { id = "q" + i, question = new string('中', 2000), options = Enumerable.Range(0, 8).Select(j => j + new string('文', 199)), allowMultiple = true }) });
        var broker = new UserInputBroker();
        var request = broker.Create(Identity, 1, "call", 1, args);
        Assert.True(broker.Validate(request, Reply(request, new QuestionAnswer("q0", [0, 7], null), new("q1", [1], null), new("q2", [], "自由文本")), Scope).Success);
    }

    [Theory]
    [InlineData("run")]
    [InlineData("request")]
    [InlineData("revision")]
    [InlineData("scope")]
    [InlineData("expired")]
    public void User_answers_require_current_host_binding(string mismatch)
    {
        var clock = new ManualTime();
        var broker = new UserInputBroker(clock);
        var request = QuestionRequest(broker);
        var reply = Reply(request, new QuestionAnswer("q", [], "answer"));
        var scope = Scope;
        if (mismatch == "run") reply = reply with { RunId = "other" };
        if (mismatch == "request") reply = reply with { RequestId = "other" };
        if (mismatch == "revision") reply = reply with { ExpectedRevision = 2 };
        if (mismatch == "scope") scope = Scope with { ProjectId = "other" };
        if (mismatch == "expired") clock.Now = request.Binding.ExpiresAt;
        var error = Assert.Throws<AgentStateException>(() => broker.Validate(request, reply, scope));
        Assert.Equal(mismatch == "expired" ? "RUN_INTERACTION_EXPIRED" : "RUN_INTERACTION_STALE", error.Code);
    }

    [Fact]
    public void Question_answers_must_be_complete_unique_valid_and_nonempty()
    {
        var broker = new UserInputBroker();
        var request = QuestionRequest(broker, options: ["A", "B"]);
        var invalid = new[] { Reply(request), Reply(request, new QuestionAnswer("other", [], "text")),
            Reply(request, new QuestionAnswer("q", [], " ")), Reply(request, new QuestionAnswer("q", [0, 0], null)),
            Reply(request, new QuestionAnswer("q", [0, 1], null)), Reply(request, new QuestionAnswer("q", [-1], null)),
            Reply(request, new QuestionAnswer("q", [2], null)), Reply(request, new QuestionAnswer("q", [], "a"), new("q", [], "b")) };
        foreach (var reply in invalid)
            Assert.Equal("RUN_INVALID_ANSWERS", Assert.Throws<AgentStateException>(() => broker.Validate(request, reply, Scope)).Code);
    }

    [Fact]
    public void Total_answer_limit_includes_selected_labels_and_free_text()
    {
        var broker = new UserInputBroker();
        var request = QuestionRequest(broker, options: ["中文"]);
        Assert.True(broker.Validate(request, Reply(request, new QuestionAnswer("q", [0], new string('中', 11998))), Scope).Success);
        Assert.Throws<AgentStateException>(() => broker.Validate(request, Reply(request, new QuestionAnswer("q", [0], new string('中', 11999))), Scope));
    }

    [Theory]
    [InlineData("中")]
    [InlineData("😀")]
    public void Twelve_thousand_utf16_units_have_bounded_valid_tool_payload(string character)
    {
        var broker = new UserInputBroker();
        var request = QuestionRequest(broker);
        var text = string.Concat(Enumerable.Repeat(character, 12000 / character.Length));
        var result = broker.Validate(request, Reply(request, new QuestionAnswer("q", [], text)), Scope);
        Assert.Equal(text, result.Payload.GetProperty("answers")[0].GetProperty("text").GetString());
        Assert.InRange(System.Text.Encoding.UTF8.GetByteCount(result.Payload.GetRawText()), 1, 65536);
        Assert.Throws<AgentStateException>(() => broker.Validate(request, Reply(request, new QuestionAnswer("q", [], text + character)), Scope));
    }

    [Fact]
    public void Supplementary_payload_preserves_quotes_backslashes_and_controls_as_data()
    {
        var broker = new UserInputBroker();
        var request = QuestionRequest(broker);
        var text = "😀\"quoted\"\\path\n\t";
        var result = broker.Validate(request, Reply(request, new QuestionAnswer("q", [], text)), Scope);
        using var parsed = JsonDocument.Parse(result.Payload.GetRawText());
        Assert.Equal(text, parsed.RootElement.GetProperty("answers")[0].GetProperty("text").GetString());
    }

    [Fact]
    public void Unpaired_surrogates_are_rejected_in_questions_and_answers()
    {
        var broker = new UserInputBroker();
        using var malformed = JsonDocument.Parse("{\"questions\":[{\"id\":\"q\",\"question\":\"\\ud800\"}]}");
        Assert.Throws<AgentStateException>(() => broker.Create(Identity, 1, "call", 1, malformed.RootElement));
        var request = QuestionRequest(broker);
        Assert.Throws<AgentStateException>(() => broker.Validate(request, Reply(request, new QuestionAnswer("q", [], "\ud800")), Scope));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(3601)]
    public void Brokers_reject_unbounded_or_zero_wait_ttl(int seconds)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new UserInputBroker(ttl: TimeSpan.FromSeconds(seconds)));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ApprovalBroker(ttl: TimeSpan.FromSeconds(seconds)));
    }

    [Fact]
    public void Approval_canonicalizes_recursive_properties_but_preserves_array_order()
    {
        var broker = new ApprovalBroker(new ManualTime());
        var first = Approval(broker, "{\"z\":[{\"b\":2,\"a\":1},3],\"a\":\"中文\"}");
        var second = Approval(broker, "{\"a\":\"中文\",\"z\":[{\"a\":1,\"b\":2},3]}");
        var changed = Approval(broker, "{\"a\":\"中文\",\"z\":[3,{\"a\":1,\"b\":2}]}");
        Assert.Equal(first.NormalizedArgumentsJson, second.NormalizedArgumentsJson);
        Assert.Equal(first.Tool.ArgumentsFingerprint, second.Tool.ArgumentsFingerprint);
        Assert.NotEqual(first.Tool.ArgumentsFingerprint, changed.Tool.ArgumentsFingerprint);
        Assert.Equal(64, first.Tool.ArgumentsFingerprint.Length);
        Assert.Equal(64, first.Tool.SchemaFingerprint.Length);
        Assert.Equal("firstparty.fixture", first.Tool.PluginId);
        Assert.Equal("1.2.3", first.Tool.PluginVersion);
        Assert.Equal("fixture.command", first.Tool.ToolName);
        Assert.Equal("root-auth", first.Tool.RootAuthorizationId);
        Assert.Equal(Identity.AuthorizationRevision, first.Tool.AuthorizationRevision);
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("{\"a\":1,\"a\":2}")]
    [InlineData("{\"a\":{\"b\":1,\"b\":2}}")]
    public void Approval_rejects_nonobject_and_duplicate_parameters(string json)
    {
        Assert.Equal("RUN_INVALID_APPROVAL", Assert.Throws<AgentStateException>(() => Approval(new(), json)).Code);
    }

    [Fact]
    public void Approval_rejects_excessive_depth_and_canonical_utf8_size()
    {
        var deep = "{\"a\":" + string.Concat(Enumerable.Repeat("{\"a\":", 33)) + "0" + new string('}', 34);
        Assert.Throws<AgentStateException>(() => Approval(new(), deep));
        var large = JsonSerializer.Serialize(new { text = new string('中', 22000) });
        Assert.Throws<AgentStateException>(() => Approval(new(), large));
    }

    [Theory]
    [InlineData("run")]
    [InlineData("request")]
    [InlineData("revision")]
    [InlineData("scope")]
    [InlineData("expired")]
    [InlineData("decision")]
    public void Approval_reply_requires_frozen_binding_and_defined_decision(string mismatch)
    {
        var clock = new ManualTime();
        var broker = new ApprovalBroker(clock);
        var request = Approval(broker, "{}");
        var reply = new ApprovalReply("run", request.Binding.RequestId, 1, ApprovalDecision.Allow);
        var scope = Scope;
        if (mismatch == "run") reply = reply with { RunId = "other" };
        if (mismatch == "request") reply = reply with { RequestId = "other" };
        if (mismatch == "revision") reply = reply with { ExpectedRevision = 2 };
        if (mismatch == "scope") scope = Scope with { RoleId = "other" };
        if (mismatch == "expired") clock.Now = request.Binding.ExpiresAt;
        if (mismatch == "decision") reply = reply with { Decision = (ApprovalDecision)99 };
        Assert.Throws<AgentStateException>(() => broker.Validate(request, reply, scope));
    }

    [Fact]
    public void Approval_returns_allow_or_deny_without_invoking_the_provider()
    {
        var broker = new ApprovalBroker();
        var request = Approval(broker, "{}");
        Assert.Equal(ApprovalDecision.Allow, broker.Validate(request, new("run", request.Binding.RequestId, 1, ApprovalDecision.Allow), Scope));
        Assert.Equal(ApprovalDecision.Deny, broker.Validate(request, new("run", request.Binding.RequestId, 1, ApprovalDecision.Deny), Scope));
    }

    [Fact]
    public void Approval_does_not_freeze_schema_invalid_arguments()
    {
        var tool = new RegisteredTool("firstparty.fixture", new("fixture.command", "Synthetic command",
            "{\"type\":\"object\",\"properties\":{\"version\":{\"type\":\"integer\"}},\"required\":[\"version\"],\"additionalProperties\":false}",
            ToolEffect.Command), new NeverInvoked());
        var broker = new ApprovalBroker();
        Assert.Throws<AgentStateException>(() => broker.Create(Identity, 1, "call", 1, tool, "1.0.0",
            JsonSerializer.SerializeToElement(new { version = "model" })));
    }

    [Fact]
    public void Model_authority_fields_cannot_replace_host_binding()
    {
        var request = Approval(new(), "{\"RunId\":\"model\",\"rootAuthorizationId\":\"model-root\",\"AuthorizationRevision\":999}");
        Assert.Equal("run", request.Binding.Identity.RunId);
        Assert.Equal("root-auth", request.Tool.RootAuthorizationId);
        Assert.Equal(7, request.Tool.AuthorizationRevision);
    }

    [Fact]
    public void Canonical_arguments_accept_exact_64kib_and_reject_next_byte()
    {
        var accepted = Approval(new(), JsonSerializer.Serialize(new { text = new string('a', 65525) }));
        Assert.Equal(65536, System.Text.Encoding.UTF8.GetByteCount(accepted.NormalizedArgumentsJson));
        Assert.Throws<AgentStateException>(() => Approval(new(), JsonSerializer.Serialize(new { text = new string('a', 65526) })));
    }

    [Fact]
    public void Unicode_string_spellings_produce_identical_fingerprints()
    {
        var literal = Approval(new(), "{\"text\":\"中😀\"}");
        var escaped = Approval(new(), "{\"text\":\"\\u4e2d\\ud83d\\ude00\"}");
        Assert.Equal(literal.Tool.ArgumentsFingerprint, escaped.Tool.ArgumentsFingerprint);
        var broker = new UserInputBroker();
        var request = QuestionRequest(broker);
        var text = "回答😀";
        Assert.Equal(text, broker.Validate(request, Reply(request, new QuestionAnswer("q", [], text)), Scope)
            .Payload.GetProperty("answers")[0].GetProperty("text").GetString());
    }

    [Theory]
    [InlineData(1)]
    [InlineData(3600)]
    public void Exact_ttl_endpoints_are_finite_and_accepted(int seconds)
    {
        var clock = new ManualTime();
        var broker = new UserInputBroker(clock, TimeSpan.FromSeconds(seconds));
        Assert.Equal(clock.Now.AddSeconds(seconds), QuestionRequest(broker).Binding.ExpiresAt);
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(1, -1)]
    public void Invalid_host_step_and_revision_are_rejected(int step, long revision)
    {
        var args = JsonSerializer.SerializeToElement(new { questions = new[] { new { id = "q", question = "question" } } });
        Assert.Equal("RUN_INVALID_INTERACTION", Assert.Throws<AgentStateException>(() => new UserInputBroker()
            .Create(Identity, step, "call", revision, args)).Code);
    }

    [Fact]
    public void Canonical_json_depth_counts_containers_including_empty_objects()
    {
        static string Nested(int depth) => string.Concat(Enumerable.Repeat("{\"a\":", depth - 1)) + "{}" + new string('}', depth - 1);
        Assert.NotNull(Approval(new(), Nested(32)));
        Assert.Throws<AgentStateException>(() => Approval(new(), Nested(33)));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("---")]
    [InlineData("1\n2")]
    public void Approval_rejects_invalid_provider_version(string version)
    {
        var tool = new RegisteredTool("firstparty.fixture", new("fixture.command", "Synthetic command", "{\"type\":\"object\"}", ToolEffect.Command), new NeverInvoked());
        Assert.Throws<AgentStateException>(() => new ApprovalBroker().Create(Identity, 1, "call", 1, tool, version, JsonSerializer.SerializeToElement(new { })));
    }

    private static UserInputRequest QuestionRequest(UserInputBroker broker, string[]? options = null) =>
        broker.Create(Identity, 1, "call", 1, JsonSerializer.SerializeToElement(new
        { questions = new[] { new { id = "q", question = "Question?", options = options ?? [] } } }));
    private static UserInputReply Reply(UserInputRequest request, params QuestionAnswer[] answers) =>
        new("run", request.Binding.RequestId, request.Binding.WaitingRevision, [.. answers]);
    private static ApprovalRequest Approval(ApprovalBroker broker, string json)
    {
        using var document = JsonDocument.Parse(json);
        return broker.Create(Identity, 1, "call", 1,
            new("firstparty.fixture", new("fixture.command", "Synthetic command", "{\"type\":\"object\"}", ToolEffect.Command), new NeverInvoked()),
            "1.2.3", document.RootElement, "root-auth");
    }
    private sealed class NeverInvoked : IToolProvider
    {
        public ToolDescriptor Descriptor => throw new InvalidOperationException("Do not resolve provider descriptor.");
        public ValueTask<ToolResult> InvokeAsync(ToolInvocation invocation, CancellationToken token) =>
            throw new InvalidOperationException("Pure broker must never invoke.");
    }
    private sealed class ManualTime : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 10, 6, 0, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }
}
