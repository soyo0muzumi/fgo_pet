using System.Text.Json;
using FgoPet.App.Dialogue;
using FgoPet.Dialogue.Contracts;
using FgoPet.Extensibility;
using FgoPet.Kernel.Agent;
using FgoPet.UiSdk;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace FgoPet.App.Tests.Dialogue;

public sealed partial class NativeConversationIntegrationTests
{
    [Theory]
    [InlineData("reminder.create", "reminders")]
    [InlineData("reminder.cancel", "reminders")]
    [InlineData("focus.resume", "firstparty.focus")]
    public Task Native_approval_card_displays_frozen_owner_data_and_only_native_allow_executes_once(string toolName, string pluginId) => RunInputChatTest(async () =>
    {
        var tool = new ApprovalFixtureTool(toolName, pluginId);
        var provider = new ScriptedProvider(ProviderTurn.Call(toolName, "operation", "{\"target\":\"synthetic-id\"}"), ProviderTurn.Final("finished"));
        await using var host = await TestHost.CreateAsync(provider, null, new RecordingObserver(), approvalPlugin: tool);
        using var model = new ConversationViewModel(host.Orchestrator, host.Settings);
        model.SetActiveServant(RoleId); model.InputText = "Synthetic operation";
        await model.SendCommand.ExecuteAsync(null);
        var native = new ApprovalChatHost { Decision = ChatApprovalDecision.Allow };
        using var session = new ChatWebSession(model, native);
        var card = ApprovalCard(session);
        Assert.Equal(toolName, card.GetProperty("toolName").GetString());
        var details = card.GetProperty("plainTextDetails").GetString()!;
        Assert.Contains("synthetic-id", details); Assert.Contains("17", details);
        if (pluginId == "reminders") Assert.Contains(ApprovalFixtureTool.Summary, details);
        Assert.False(string.IsNullOrWhiteSpace(card.GetProperty("actionTitle").GetString()));
        var summary = card.GetProperty("plainTextSummary").GetString()!;
        if (pluginId == "reminders") Assert.Equal(ApprovalFixtureTool.Summary, summary);
        else { Assert.Contains("synthetic-id", summary); Assert.Contains("17", summary); Assert.Contains("remaining time", summary); }
        Assert.Equal(0, tool.Invocations); Assert.False(model.CanSend);
        var review = InputCommand(session, "chat.approval.review", card);
        Assert.True((await session.HandleCommandAsync(review, default)).Success);
        await AwaitInputCompletion(model);
        Assert.Equal(1, native.Reviews); Assert.Equal(1, tool.Invocations);
        Assert.False((await session.HandleCommandAsync(review, default)).Success);
        Assert.Equal(1, tool.Invocations);
    });

    [Theory]
    [InlineData("chat.approval.deny")]
    [InlineData("chat.approval.cancel")]
    [InlineData("unavailable-host")]
    public Task Refusal_cancel_and_hosts_without_native_review_never_execute(string action) => RunInputChatTest(async () =>
    {
        var tool = new ApprovalFixtureTool("reminder.cancel", "reminders");
        var provider = new ScriptedProvider(ProviderTurn.Call(tool.Descriptor.Name, "operation", "{\"target\":\"synthetic-id\"}"), ProviderTurn.Final("not executed"));
        await using var host = await TestHost.CreateAsync(provider, null, new RecordingObserver(), approvalPlugin: tool);
        using var model = new ConversationViewModel(host.Orchestrator, host.Settings);
        model.SetActiveServant(RoleId); model.InputText = "Synthetic operation";
        await model.SendCommand.ExecuteAsync(null);
        using var session = new ChatWebSession(model, new InputChatHost());
        var message = InputCommand(session, action == "unavailable-host" ? "chat.approval.review" : action, ApprovalCard(session));
        Assert.True((await session.HandleCommandAsync(message, default)).Success);
        await AwaitInputCompletion(model);
        Assert.Equal(0, tool.Invocations); Assert.False(model.HasPendingRun);
        Assert.False((await session.HandleCommandAsync(message, default)).Success);
    });

    [Theory]
    [InlineData("deny")]
    [InlineData("cancel")]
    [InlineData("stop")]
    [InlineData("expiry")]
    [InlineData("conversation")]
    [InlineData("role")]
    [InlineData("project")]
    [InlineData("surface")]
    public Task Native_allow_returning_after_revocation_is_rejected(string revoke) => RunInputChatTest(async () =>
    {
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var tool = new ApprovalFixtureTool("reminder.cancel", "reminders");
        var provider = new ScriptedProvider(ProviderTurn.Call(tool.Descriptor.Name, "operation", "{\"target\":\"synthetic-id\"}"), ProviderTurn.Final("not executed"));
        await using var host = await TestHost.CreateAsync(provider, null, new RecordingObserver(), time: clock, approvalPlugin: tool);
        using var model = new ConversationViewModel(host.Orchestrator, host.Settings, time: clock);
        model.SetActiveServant(RoleId); model.InputText = "Synthetic operation";
        await model.SendCommand.ExecuteAsync(null);
        var native = new ApprovalChatHost { Completion = new(TaskCreationOptions.RunContinuationsAsynchronously) };
        using var session = new ChatWebSession(model, native);
        var card = ApprovalCard(session);
        var message = InputCommand(session, "chat.approval.review", card);
        var pending = session.HandleCommandAsync(message, default).AsTask();
        Assert.Equal(1, native.Reviews); Assert.False(pending.IsCompleted);
        Assert.False((await session.HandleCommandAsync(message, default)).Success);
        switch (revoke)
        {
            case "deny": Assert.True((await session.HandleCommandAsync(InputCommand(session, "chat.approval.deny", card), default)).Success); break;
            case "cancel": Assert.True((await session.HandleCommandAsync(InputCommand(session, "chat.approval.cancel", card), default)).Success); break;
            case "stop": model.StopCommand.Execute(null); break;
            case "expiry": clock.Advance(TimeSpan.FromMinutes(16)); await Task.Delay(30); break;
            case "conversation": model.NewConversationCommand.Execute(null); break;
            case "role": model.SetActiveServant("different-role"); break;
            case "project": model.SessionContext.TrySetProject("different-project", "Synthetic"); break;
            case "surface": session.Dispose(); break;
        }
        native.Completion.SetResult(ChatApprovalDecision.Allow);
        Assert.False((await pending).Success);
        await AwaitInputCompletion(model);
        Assert.Equal(0, tool.Invocations);
    });

    [Theory]
    [InlineData("runId")]
    [InlineData("requestId")]
    [InlineData("revision")]
    public Task Approval_stale_binding_is_rejected_before_opening_native_dialog(string field) => RunInputChatTest(async () =>
    {
        var tool = new ApprovalFixtureTool("reminder.create", "reminders");
        var provider = new ScriptedProvider(ProviderTurn.Call(tool.Descriptor.Name, "operation", "{\"target\":\"synthetic-id\"}"));
        await using var host = await TestHost.CreateAsync(provider, null, new RecordingObserver(), approvalPlugin: tool);
        using var model = new ConversationViewModel(host.Orchestrator, host.Settings);
        model.SetActiveServant(RoleId); model.InputText = "Synthetic operation";
        await model.SendCommand.ExecuteAsync(null);
        var native = new ApprovalChatHost { Decision = ChatApprovalDecision.Allow };
        using var session = new ChatWebSession(model, native);
        var message = InputCommand(session, "chat.approval.review", ApprovalCard(session));
        var values = JsonSerializer.Deserialize<Dictionary<string, object>>(message.Payload.GetRawText())!;
        values[field] = field == "revision" ? 123456 : "different";
        Assert.False((await session.HandleCommandAsync(message with { Payload = JsonSerializer.SerializeToElement(values) }, default)).Success);
        Assert.Equal(0, native.Reviews); Assert.Equal(0, tool.Invocations);
    });

    private static JsonElement ApprovalCard(ChatWebSession session) => JsonSerializer.SerializeToElement(session.ReadSnapshot(),
        new JsonSerializerOptions(JsonSerializerDefaults.Web)).GetProperty("conversation").GetProperty("pendingApproval");

    private sealed class ApprovalChatHost : IChatWebHostActions
    {
        public int Reviews { get; private set; }
        public ChatApprovalDecision Decision { get; init; }
        public TaskCompletionSource<ChatApprovalDecision>? Completion { get; init; }
        public event EventHandler? Changed { add { } remove { } }
        public ChatWebHostPresentation ReadPresentation() => new("Synthetic", null, [], []);
        public ValueTask<WebSurfaceCommandResult> HandleAsync(ChatWebHostRequest request, CancellationToken token) => ValueTask.FromResult(new WebSurfaceCommandResult(false));
        public ValueTask<ChatApprovalDecision> ReviewApprovalAsync(ChatWebApproval approval, CancellationToken token)
        {
            Reviews++;
            return Completion is null ? ValueTask.FromResult(Decision) : new(Completion.Task);
        }
    }

    private sealed class ApprovalFixtureTool(string name, string owner) : IFgoPetPlugin, IToolProvider,
        IToolBusinessConfirmationProvider, IToolResourceAuthorizationProvider
    {
        public const string Summary = "Synthetic <img src=x onerror=alert(1)> reminder\n2027-01-02T08:00:00+08:00\nUTC: 2027-01-02T00:00:00Z\nAsia/Shanghai\nID: synthetic-id\nVersion: 17";
        public int Invocations { get; private set; }
        public PluginManifest Manifest { get; } = new(owner, "1.0.0", 1, []);
        public PluginContributions Contributions => PluginContributions.Empty with { Tools = [this] };
        public ToolDescriptor Descriptor { get; } = new(name, "Synthetic bounded operation",
            """{"type":"object","properties":{"target":{"type":"string"}},"required":["target"],"additionalProperties":false}""", ToolEffect.Command);
        public ToolResourceAuthorization GetAuthorization(ToolScope scope) => new("synthetic-id", 17);
        public ToolBusinessConfirmation PrepareConfirmation(ToolInvocation invocation, CancellationToken token) => new("synthetic-id", 17, new string('A', 64)) { PlainTextSummary = Summary };
        public void ValidateConfirmation(ToolInvocation invocation, ToolBusinessConfirmation confirmation, CancellationToken token)
        { if (confirmation != PrepareConfirmation(invocation, token)) throw new ToolBusinessConfirmationException("CHANGED"); }
        public ValueTask<ToolResult> InvokeAsync(ToolInvocation invocation, CancellationToken token)
        { token.ThrowIfCancellationRequested(); Invocations++; return ValueTask.FromResult(new ToolResult(true, JsonSerializer.SerializeToElement(new { completed = true })) { ExecutionState = ToolExecutionState.Committed }); }
        public ValueTask StartAsync(CancellationToken token) => ValueTask.CompletedTask;
        public ValueTask StopAsync(CancellationToken token) => ValueTask.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
