using System.Collections.Concurrent;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Encodings.Web;
using FgoPet.App.Dialogue;
using FgoPet.Core.Dialogue;
using FgoPet.Core.Packs;
using FgoPet.Core.Settings;
using FgoPet.Dialogue.Settings;
using FgoPet.Dialogue.Contracts;
using FgoPet.Extensibility;
using FgoPet.Infrastructure.Dialogue;
using FgoPet.Infrastructure.Packs;
using FgoPet.Infrastructure.Persistence;
using FgoPet.Infrastructure.Providers;
using FgoPet.Kernel.Agent;
using FgoPet.Kernel.Conversation;
using FgoPet.Platform.Secrets;
using FgoPet.UiSdk;
using System.Windows.Threading;
using Microsoft.Extensions.Time.Testing;
using Microsoft.Extensions.Logging;
using Xunit;

namespace FgoPet.App.Tests.Dialogue;

public sealed partial class NativeConversationIntegrationTests
{
    private const string RoleId = "800100";
    private const string ProjectId = "integration-project";

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Dispatch_diagnostics_record_actual_envelope_without_private_content(bool toolsRejected)
    {
        var provider = toolsRejected
            ? new ScriptedProvider(ProviderTurn.Fail(new(ProviderFailureCategory.ToolsRejected, "fixture rejection")), ProviderTurn.Final("done"))
            : new ScriptedProvider(ProviderTurn.Final("done"));
        var logger = new ContextLogger();
        await using var host = await TestHost.CreateAsync(provider, new RecordingTool(ToolEffect.ReadOnly),
            new RecordingObserver(), context: new ChangingContext(), logger: logger);
        var result = await host.Orchestrator.SendAsync(RoleId, "private-user-sentinel", default, new(ProjectId));
        Assert.Equal(ConversationSendStatus.Completed, result.Status);
        Assert.Contains(logger.Entries, entry => entry.Contains("phase=dispatch", StringComparison.Ordinal)
            && entry.Contains("personaCore=True", StringComparison.Ordinal));
        Assert.Equal(provider.Requests.Count, logger.Dispatches.Count);
        for (var index = 0; index < provider.Requests.Count; index++)
        {
            Assert.Equal(provider.Requests[index].ModelMessages.Length, logger.Dispatches[index]["TotalMessageCount"]);
            Assert.Equal(provider.Requests[index].Tools?.Count ?? 0, logger.Dispatches[index]["ToolDefinitionCount"]);
            Assert.Equal(new RequestTokenMeter().Measure(ModelRouteKey.From(host.Settings.Load().ModelConnection!),
                provider.Requests[index]).InputTokens, logger.Dispatches[index]["TotalInputTokens"]);
        }
        Assert.All(logger.Entries, entry =>
        {
            Assert.DoesNotContain("private-user-sentinel", entry);
            Assert.DoesNotContain("revision=1;", entry);
            Assert.DoesNotContain(result.RunId!, entry);
            Assert.DoesNotContain(result.ConversationId, entry);
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Running_tool_context_compacts_with_same_summary_service_without_covering_live_user(bool summaryFails)
    {
        var provider = new ScriptedProvider(ProviderTurn.Call("fixture.read", "one", "{}"),
            ProviderTurn.Call("fixture.read", "two", "{}"), ProviderTurn.Call("fixture.read", "three", "{}"),
            ProviderTurn.Final("读取完成。"));
        var summaries = new NativeSummaryRecorder(summaryFails);
        var context = new ChangingContext();
        await using var host = await TestHost.CreateAsync(provider, new RecordingTool(ToolEffect.ReadOnly, new string('x', 8000)),
            new RecordingObserver(), TestSettingsStore.Create(20000), enableSummary: true, summarizer: summaries, context: context);
        var result = await host.Orchestrator.SendAsync(RoleId, "保留这个当前问题，逐段读取", default, new(ProjectId));
        if (summaryFails)
        {
            Assert.Equal(ConversationSendStatus.Failed, result.Status);
            Assert.NotEmpty(summaries.Requests);
            Assert.True(provider.Requests.Count < 4);
            var failed = await host.Runs.LoadAsync(result.RunId!, default);
            Assert.True(failed!.CompletedSteps.Sum(step => step.Calls.Length) >= 2);
            Assert.Null(host.ContextStore!.Read(new(RoleId, ProjectId), result.ConversationId).Summary);
            Assert.DoesNotContain(host.Conversations.LoadMessages(result.ConversationId, RoleId), message => message.Role == ChatMessageRole.Assistant);
            return;
        }
        Assert.Equal(ConversationSendStatus.Completed, result.Status);
        Assert.True(summaries.Requests.Count >= 2);
        Assert.All(provider.Requests, request => Assert.Contains(request.ModelMessages,
            message => message.Role == ModelMessageRole.User && message.Content.Contains("保留这个当前问题", StringComparison.Ordinal)));
        Assert.All(provider.Requests, request => Assert.Single(request.ModelMessages.Where(message =>
            message.Role == ModelMessageRole.System && message.Content.Contains("You are the desktop companion", StringComparison.Ordinal))));
        Assert.Contains(provider.Requests[^1].ModelMessages, message => message.Content.Contains("run_context_summary", StringComparison.Ordinal));
        Assert.Null(host.ContextStore!.Read(new(RoleId, ProjectId), result.ConversationId).Summary);
        var checkpoint = await host.Runs.LoadAsync(result.RunId!, default);
        Assert.Equal(provider.Requests.Count + summaries.Requests.Count, checkpoint!.Snapshot.ModelRequests);
        Assert.Equal(3, checkpoint.CompletedSteps.SelectMany(step => step.Calls).Count(call => call.Call.Name != FinalizeTool));
        Assert.Single(checkpoint.CompletedSteps.SelectMany(step => step.Calls), call => call.Call.Name == FinalizeTool);
        Assert.Equal(provider.Requests.Count, context.Count);
        for (var index = 0; index < provider.Requests.Count; index++)
        {
            var memory = Assert.Single(provider.Requests[index].ModelMessages.Where(message =>
                message.Content.Contains("fixture.native-conversation:confirmed-memory:records", StringComparison.Ordinal)));
            Assert.Contains($"revision={index + 1};", memory.Content);
            Assert.DoesNotContain("CONTEXT_TRUNCATED", memory.Content);
        }
    }

    [Fact]
    public async Task Different_project_never_receives_previous_tool_context()
    {
        var provider = new ScriptedProvider(ProviderTurn.Call("fixture.read", "one", "{}"), ProviderTurn.Final("已读。"), ProviderTurn.Final("新项目。"));
        await using var host = await TestHost.CreateAsync(provider, new RecordingTool(ToolEffect.ReadOnly), new RecordingObserver());
        var first = await host.Orchestrator.SendAsync(RoleId, "read", default, new(ProjectId));
        var next = await host.Orchestrator.SendAsync(RoleId, "hello", default, new("other-project"));
        Assert.Equal(ConversationSendStatus.Completed, next.Status);
        Assert.NotEqual(first.ConversationId, next.ConversationId);
        Assert.DoesNotContain(provider.Requests[^1].ModelMessages, message => message.Role == ModelMessageRole.Tool && message.ToolCallId == "one");
        Assert.Empty(NativeHistoryEvidence.Read(provider.Requests[^1].ModelMessages));
    }

    [Fact]
    public async Task Next_send_retains_tool_evidence_without_reexecuting_or_saving_it_as_chat()
    {
        var provider = new ScriptedProvider(ProviderTurn.Call("fixture.read", "same-id", "{}"),
            ProviderTurn.Final("已读取。"), ProviderTurn.Call("fixture.read", "same-id", "{}"),
            ProviderTurn.Final("已核对。"));
        var tool = new RecordingTool(ToolEffect.ReadOnly);
        await using var host = await TestHost.CreateAsync(provider, tool, new RecordingObserver(), TestSettingsStore.Create(131072));
        var first = await host.Orchestrator.SendAsync(RoleId, "读取", default, new(ProjectId));
        var second = await host.Orchestrator.SendAsync(RoleId, "刚才读取的细节？", default, new(ProjectId));
        Assert.Equal(ConversationSendStatus.Completed, first.Status);
        Assert.Equal(ConversationSendStatus.Completed, second.Status);
        var secondInput = provider.Requests[3].ModelMessages;
        ModelProtocol.ValidateTranscript(secondInput);
        Assert.Equal("tool observation", Assert.Single(NativeHistoryEvidence.Read(secondInput)).GetProperty("observation").GetProperty("payload").GetProperty("value").GetString());
        Assert.DoesNotContain(secondInput.SelectMany(m => m.ToolCalls), c => c.CallId == "same-id");
        Assert.Equal(2, tool.Invocations.Count);
        Assert.DoesNotContain(host.Conversations.LoadMessages(first.ConversationId, RoleId), m => m.Text.Contains("tool observation", StringComparison.Ordinal));
    }

    [Fact]
    public async Task System_clock_observation_reaches_native_model_but_is_not_semantic_history()
    {
        await using var clock = new FgoPet.Plugin.DesktopContext.DesktopContextPlugin(_ => true);
        await clock.StartAsync(default);
        var provider = new ScriptedProvider(ProviderTurn.Call("context.time", "clock", "{}"), ProviderTurn.Final("已按本机时钟读取。"));
        await using var host = await TestHost.CreateAsync(provider, Assert.Single(clock.Contributions.Tools), new RecordingObserver());
        var before = DateTimeOffset.UtcNow;
        var result = await host.Orchestrator.SendAsync(RoleId, "现在几点", default, new(ProjectId));
        var after = DateTimeOffset.UtcNow;
        Assert.Equal(ConversationSendStatus.Completed, result.Status);
        var message = Assert.Single(provider.Requests[1].ModelMessages.Where(m => m.Role == ModelMessageRole.Tool));
        using var observation = System.Text.Json.JsonDocument.Parse(message.Content);
        var payload = observation.RootElement.GetProperty("payload");
        Assert.InRange(payload.GetProperty("sampledAtUtc").GetDateTimeOffset(), before, after);
        Assert.Equal(TimeZoneInfo.Local.Id, payload.GetProperty("timeZoneId").GetString());
        Assert.DoesNotContain(host.Conversations.LoadMessages(result.ConversationId, RoleId), m => m.Text.Contains("sampledAtUtc", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Tool_call_attempt_in_reserved_final_round_never_executes()
    {
        var provider = new ScriptedProvider(Enumerable.Range(0, 32).Select(i => ProviderTurn.Call("fixture.read", "read-" + i, "{}")).ToArray());
        var tool = new RecordingTool(ToolEffect.ReadOnly);
        await using var host = await TestHost.CreateAsync(provider, tool, new RecordingObserver(), TestSettingsStore.Create(131072));
        var result = await host.Orchestrator.SendAsync(RoleId, "read", default, new(ProjectId));
        Assert.Equal(ConversationSendStatus.Failed, result.Status);
        Assert.Equal(31, tool.Invocations.Count);
        Assert.Null(provider.Requests[^1].Tools);
    }

    [Fact]
    public async Task Cancel_then_resend_in_same_conversation_keeps_distinct_identity_on_every_event()
    {
        var gate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var provider = new ScriptedProvider(ProviderTurn.BlockedFinal("stale", gate), ProviderTurn.Final("fresh"));
        await using var host = await TestHost.CreateAsync(provider, new RecordingTool(ToolEffect.ReadOnly), new RecordingObserver());
        var events = new List<ConversationUpdate>();
        host.Orchestrator.Updated += events.Add;
        var first = host.Orchestrator.SendAsync(RoleId, "first", default, new(ProjectId));
        await provider.FirstRequestReceived.Task.WaitAsync(TimeSpan.FromSeconds(5));
        host.Orchestrator.CancelCurrent();
        gate.TrySetResult(true);
        var cancelled = await first.WaitAsync(TimeSpan.FromSeconds(5));
        var firstEvents = events.ToArray();
        events.Clear();
        var completed = await host.Orchestrator.SendAsync(RoleId, "second", default, new(ProjectId));
        Assert.Equal(ConversationSendStatus.Cancelled, cancelled.Status);
        Assert.Equal(ConversationSendStatus.Completed, completed.Status);
        Assert.Equal(cancelled.ConversationId, completed.ConversationId);
        var property = typeof(ConversationUpdate).GetProperty("RequestId");
        Assert.NotNull(property);
        var firstId = Assert.IsType<string>(property.GetValue(firstEvents[0]));
        var secondId = Assert.IsType<string>(property.GetValue(events[0]));
        Assert.NotEqual(firstId, secondId);
        Assert.All(firstEvents, item => Assert.Equal(firstId, property.GetValue(item)));
        Assert.All(events, item => Assert.Equal(secondId, property.GetValue(item)));
        Assert.Contains(firstEvents, item => item.Type == ConversationUpdateType.Cancelled);
        Assert.Contains(events, item => item.Type == ConversationUpdateType.AssistantCompleted);
    }

    [Fact]
    public async Task Last_native_request_retains_partial_observation_but_offers_no_more_tools()
    {
        var turns = Enumerable.Range(0, 31).Select(i => ProviderTurn.Call("fixture.read", "read-" + i, "{}"))
            .Append(ProviderTurn.Final("只读到部分内容。"));
        var provider = new ScriptedProvider(turns.ToArray());
        var tool = new RecordingTool(ToolEffect.ReadOnly);
        await using var host = await TestHost.CreateAsync(provider, tool, new RecordingObserver(), TestSettingsStore.Create(131072));
        var result = await host.Orchestrator.SendAsync(RoleId, "read", default, new(ProjectId));
        Assert.Equal(ConversationSendStatus.Completed, result.Status);
        var last = provider.Requests[^1];
        Assert.Null(last.Tools);
        Assert.Contains(last.ModelMessages, m => m.Role == ModelMessageRole.System && m.Content.Contains("partial coverage", StringComparison.Ordinal));
        Assert.Contains(last.ModelMessages, m => m.Role == ModelMessageRole.Tool && m.Content.Contains("tool observation", StringComparison.Ordinal));
        Assert.Equal(31, tool.Invocations.Count);
        Assert.Equal(32, provider.Requests.Count);
        Assert.NotNull(provider.Requests[7].Tools);
        var checkpoint = await host.Runs.LoadAsync(result.RunId!, default);
        Assert.Equal(32, checkpoint!.Snapshot.Budget.MaxModelRequests);
        Assert.Equal(64, checkpoint.Snapshot.Budget.MaxToolCalls);
    }

    [Fact]
    public async Task Chrome_snapshot_returns_to_same_native_loop_without_becoming_semantic_history()
    {
        await using var browser = new FgoPet.Plugin.Browser.ChromeBrowserPlugin(
            () => new(true, "node.exe", "server.js"), _ => true, new BrowserConnection());
        await browser.StartAsync(default);
        var read = browser.Contributions.Tools.Single(t => t.Descriptor.Name == "browser.read");
        var provider = new ScriptedProvider(
            ProviderTurn.Call("browser.read", "browser-read", "{\"pageId\":7}"),
            ProviderTurn.Final("我读到了页面里的原句。"));
        await using var host = await TestHost.CreateAsync(provider, read, new RecordingObserver());

        var result = await host.Orchestrator.SendAsync(RoleId, "读一下网页", default, new(ProjectId));

        Assert.Equal(ConversationSendStatus.Completed, result.Status);
        Assert.Equal(3, provider.Requests.Count);
        Assert.Contains(provider.Requests[1].ModelMessages, m => m.Role == ModelMessageRole.Tool
            && m.Content.Contains("fixture page original", StringComparison.Ordinal)
            && m.Content.Contains("https://example.org/", StringComparison.Ordinal)
            && m.Content.Contains("capturedAtUtc", StringComparison.Ordinal));
        var messages = host.Conversations.LoadMessages(result.ConversationId, RoleId);
        Assert.Equal(2, messages.Count());
        Assert.DoesNotContain(messages, m => m.Text.Contains("fixture page original", StringComparison.Ordinal));
    }

    private sealed class BrowserConnection : FgoPet.Plugin.Browser.IChromeMcpConnection
    {
        public ValueTask<System.Text.Json.JsonElement> CallAsync(FgoPet.Plugin.Browser.ChromeBrowserSettings settings,
            string name, System.Text.Json.JsonElement arguments, CancellationToken token) =>
            ValueTask.FromResult(System.Text.Json.JsonSerializer.SerializeToElement(name == "take_snapshot" ? (object)new {
                structuredContent = new { snapshot = new { url = "https://example.org/" } },
                content = new[] { new { type = "text", text = "fixture page original" } }
            } : new { structuredContent = new { pages = new[] { new { id = 7, url = "https://example.org/", title = "Fixture" } } } }));
        public ValueTask ResetAsync() => ValueTask.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    [Fact]
    public async Task Tool_step_then_final_persists_one_semantic_assistant_message_and_observes_once()
    {
        var tool = new RecordingTool(ToolEffect.ReadOnly);
        var observer = new RecordingObserver();
        var provider = new ScriptedProvider(
            ProviderTurn.Call("fixture.read", "call-read", "{}"),
            ProviderTurn.Final("最终答复"));
        await using var host = await TestHost.CreateAsync(provider, tool, observer);

        var result = await host.Orchestrator.SendAsync(RoleId, "读取后总结", default, new(ProjectId));

        Assert.Equal(ConversationSendStatus.Completed, result.Status);
        Assert.False(string.IsNullOrWhiteSpace(result.RunId));
        Assert.Single(tool.Invocations);
        var messages = host.Conversations.LoadMessages(result.ConversationId, RoleId);
        Assert.Collection(messages,
            message => Assert.Equal(ChatMessageRole.User, message.Role),
            message => Assert.Equal(ChatMessageRole.Assistant, message.Role));
        var assistant = Assert.Single(messages, message => message.Role == ChatMessageRole.Assistant);
        Assert.Equal(ChatMessageStatus.Completed, assistant.Status);
        Assert.Equal("最终答复", assistant.Text);
        Assert.DoesNotContain(messages, message => message.Text.Contains("tool observation", StringComparison.Ordinal));

        var requests = provider.Requests;
        Assert.Collection(requests,
            first => Assert.NotNull(first.Tools),
            second => Assert.Contains(second.ModelMessages, message => message.Role == ModelMessageRole.Tool
                && message.Content.Contains("tool observation", StringComparison.Ordinal)), AssertFinalRequest);
        var checkpoint = await host.Runs.LoadAsync(result.RunId!, default);
        Assert.Equal(AgentRunStatus.Completed, checkpoint!.Snapshot.Status);
        Assert.Equal(3, checkpoint.Snapshot.ModelRequests);
        Assert.Equal(2, checkpoint.Snapshot.ToolCalls);
        Assert.Single(observer.Turns);
        Assert.Equal(assistant.MessageId, observer.Turns[0].AssistantMessageId);
        Assert.Equal("最终答复", observer.Turns[0].AssistantText);
        Assert.Equal(1L, host.CountRows("agent_final_deliveries"));
        Assert.Empty(host.Deliveries.ReadPending());
    }

    [Theory]
    [InlineData("configuration")]
    [InlineData("session")]
    public async Task Configuration_or_session_change_fences_a_blocked_native_run(string fence)
    {
        var gate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var provider = new ScriptedProvider(ProviderTurn.BlockedFinal("stale final must not publish", gate));
        var observer = new RecordingObserver();
        await using var host = await TestHost.CreateAsync(provider, new RecordingTool(ToolEffect.ReadOnly), observer);
        var send = host.Orchestrator.SendAsync(RoleId, "等待中的请求", default, new(ProjectId));
        try
        {
            await provider.FirstRequestReceived.Task.WaitAsync(TimeSpan.FromSeconds(5));
            if (fence == "configuration")
            {
                var current = host.Settings.Load().ModelConnection!;
                host.Settings.Save(host.Settings.Load() with
                {
                    ModelConnection = new ModelConnectionSettings(current.ProviderId, current.BaseUrl,
                        "changed-model", current.ToolsSupported, current.ContextWindowOverride, current.MaxOutputTokens),
                });
            }
            else
            {
                host.Orchestrator.StartNewConversation(RoleId);
            }
        }
        finally
        {
            gate.TrySetResult(true);
        }

        var result = await send.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(ConversationSendStatus.Cancelled, result.Status);
        Assert.DoesNotContain(host.Conversations.LoadMessages(result.ConversationId, RoleId),
            message => message.Role == ChatMessageRole.Assistant && message.Status == ChatMessageStatus.Completed);
        Assert.Empty(observer.Turns);
        Assert.Equal(0L, host.CountRows("agent_final_deliveries"));
    }

    [Fact]
    public async Task Tools_rejected_by_provider_retry_without_tools_and_persist_final_once()
    {
        var provider = new ScriptedProvider(
            ProviderTurn.Fail(new(FgoPet.Infrastructure.Providers.ProviderFailureCategory.ToolsRejected,
                "synthetic tools rejection")),
            ProviderTurn.Final("不使用工具箱的答复"));
        var observer = new RecordingObserver();
        await using var host = await TestHost.CreateAsync(provider, new RecordingTool(ToolEffect.ReadOnly), observer);

        var result = await host.Orchestrator.SendAsync(RoleId, "简单回复", default, new(ProjectId));

        Assert.Equal(ConversationSendStatus.Completed, result.Status);
        var requests = provider.Requests;
        Assert.Collection(requests,
            first => Assert.NotNull(first.Tools),
            second => Assert.Null(second.Tools));
        Assert.False(host.Settings.Load().ModelConnection!.ToolsSupported);
        var assistant = Assert.Single(host.Conversations.LoadMessages(result.ConversationId, RoleId),
            message => message.Role == ChatMessageRole.Assistant);
        Assert.Equal("不使用工具箱的答复", assistant.Text);
        Assert.Single(observer.Turns);
        Assert.Equal(1L, host.CountRows("agent_final_deliveries"));
    }

    [Fact]
    public async Task Failed_native_run_never_creates_a_final_delivery_or_completed_assistant_message()
    {
        var unavailable = () => ProviderTurn.Fail(new(
            FgoPet.Infrastructure.Providers.ProviderFailureCategory.ServiceUnavailable, "synthetic provider failure"));
        var provider = new ScriptedProvider(unavailable(), unavailable(), unavailable());
        var observer = new RecordingObserver();
        await using var host = await TestHost.CreateAsync(provider, new RecordingTool(ToolEffect.ReadOnly), observer);

        var result = await host.Orchestrator.SendAsync(RoleId, "会失败", default, new(ProjectId));

        Assert.Equal(ConversationSendStatus.Failed, result.Status);
        Assert.DoesNotContain("synthetic provider failure", result.SafeError ?? string.Empty, StringComparison.Ordinal);
        Assert.Equal(0L, host.CountRows("agent_final_deliveries"));
        Assert.Empty(host.Deliveries.ReadPending());
        Assert.DoesNotContain(host.Conversations.LoadMessages(result.ConversationId, RoleId),
            message => message.Role == ChatMessageRole.Assistant && message.Status == ChatMessageStatus.Completed);
        Assert.Empty(observer.Turns);
        var checkpoint = await host.Runs.LoadAsync(result.RunId!, default);
        Assert.Equal(AgentRunStatus.Failed, checkpoint!.Snapshot.Status);
    }

    [Fact]
    public async Task Default_command_policy_denies_provider_invocation_but_allows_a_final_response()
    {
        var command = new RecordingTool(ToolEffect.Command);
        var provider = new ScriptedProvider(
            ProviderTurn.Call("fixture.read", "call-command", "{}"),
            ProviderTurn.Final("操作未授权，因此没有执行。"));
        await using var host = await TestHost.CreateAsync(provider, command, new RecordingObserver());

        var result = await host.Orchestrator.SendAsync(RoleId, "执行受限操作", default, new(ProjectId));

        Assert.Equal(ConversationSendStatus.Completed, result.Status);
        Assert.Empty(command.Invocations);
        Assert.Contains(provider.Requests[1].ModelMessages, message => message.Role == ModelMessageRole.Tool
            && message.Content.Contains("TOOL_AUTHORIZATION_DENIED", StringComparison.Ordinal));
        Assert.Single(host.Conversations.LoadMessages(result.ConversationId, RoleId),
            message => message.Role == ChatMessageRole.Assistant && message.Status == ChatMessageStatus.Completed);
    }

    [Fact]
    public async Task Native_compaction_reserves_from_the_same_durable_model_request_budget()
    {
        var tool = new RecordingTool(ToolEffect.ReadOnly);
        var provider = new ScriptedProvider(
            ProviderTurn.Call("fixture.read", "call-summary", "{}"),
            ProviderTurn.Final("压缩后答复"));
        var summarizer = new RecordingSummarizer();
        var meter = new CompactionTriggeringMeter();
        await using var host = await TestHost.CreateAsync(provider, tool, new RecordingObserver(),
            enableSummary: true, summarizer: summarizer,
            settings: TestSettingsStore.Create(contextWindow: 32768), tokenMeter: meter);
        host.SeedHistoryForCompaction();
        var seeded = host.ContextStore!.Read(new(RoleId, ProjectId), "summary-conversation");
        Assert.Equal(6, seeded.UncoveredMessages.Count);
        Assert.Equal(4504, seeded.UncoveredMessages[0].Text.Length);

        var result = await host.Orchestrator.SendAsync(RoleId, "继续当前任务", default, new(ProjectId));

        Assert.Equal(ConversationSendStatus.Completed, result.Status);
        var checkpoint = await host.Runs.LoadAsync(result.RunId!, default);
        var snapshot = host.ContextStore!.Read(new(RoleId, ProjectId), result.ConversationId);
        Assert.Equal(1, summarizer.SharedBudgetReservations);
        Assert.Equal(3, provider.Requests.Count);
        Assert.Equal(4, checkpoint!.Snapshot.ModelRequests);
        Assert.NotNull(snapshot.Summary);
        Assert.Equal("assistant-old-1", snapshot.Summary!.CoveredThroughMessageId);
    }

    [Fact]
    public async Task ViewModel_send_cancel_resend_rejects_late_updates_and_duplicate_send()
    {
        var a = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var b = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var c = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var provider = new ScriptedProvider(ProviderTurn.BlockedFinal("first answer", a),
            ProviderTurn.BlockedFinal("second answer", b), ProviderTurn.BlockedFinal("third answer", c));
        await using var host = await TestHost.CreateAsync(provider, new RecordingTool(ToolEffect.ReadOnly), new RecordingObserver());
        using var model = new ConversationViewModel(host.Orchestrator, host.Settings);
        var events = new ConcurrentQueue<ConversationUpdate>();
        var capture = Environment.GetEnvironmentVariable("FGO_PET_ACCEPTANCE_ARTIFACTS");
        var correlation = Convert.ToHexString(SHA256.HashData(Guid.NewGuid().ToByteArray()))[..12];
        void Log(string stage, string result, string? request = null)
        {
            if (string.IsNullOrWhiteSpace(capture)) return;
            Directory.CreateDirectory(capture);
            File.AppendAllText(Path.Combine(capture, "request-chain.jsonl"), System.Text.Json.JsonSerializer.Serialize(new
            { timestamp = DateTimeOffset.UtcNow, correlation, stage, result,
                request = request is null ? null : Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(request)))[..12] }) + Environment.NewLine);
        }
        host.Orchestrator.Updated += update =>
        {
            events.Enqueue(update);
            Log("event." + update.Type, "received", update.RequestId);
        };
        model.SetActiveServant(RoleId);
        model.SessionContext.TrySetProject(ProjectId, "Synthetic project");
        async Task WaitForUsers(int count)
        {
            var deadline = DateTime.UtcNow.AddSeconds(5);
            while (events.Count(e => e.Type == ConversationUpdateType.UserMessagePersisted) < count && DateTime.UtcNow < deadline)
                await Task.Delay(10);
            Assert.Equal(count, events.Count(e => e.Type == ConversationUpdateType.UserMessagePersisted));
        }
        try
        {
            Log("session.start", "synthetic-native-provider");
            model.InputText = "first";
            var first = model.SendCommand.ExecuteAsync(null);
            await provider.FirstRequestReceived.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await model.SendCommand.ExecuteAsync(null);
            Assert.Single(model.Turns, t => t.Role == ChatMessageRole.User);
            Log("duplicate.send", "rejected");
            var firstId = events.First(e => e.Type == ConversationUpdateType.UserMessagePersisted).RequestId;
            Assert.False(string.IsNullOrEmpty(firstId));
            model.StopCommand.Execute(null); a.TrySetResult(true);
            await first.WaitAsync(TimeSpan.FromSeconds(5)); Assert.False(model.IsStreaming);

            model.InputText = "second";
            var second = model.SendCommand.ExecuteAsync(null);
            await WaitForUsers(2);
            var admitted = events.Last(e => e.Type == ConversationUpdateType.UserMessagePersisted);
            Assert.NotEqual(firstId, admitted.RequestId); Assert.True(model.IsStreaming);
            var observer = (Action<ConversationUpdate>)typeof(ConversationOrchestrator)
                .GetField("Updated", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(host.Orchestrator)!;
            foreach (var type in new[] { ConversationUpdateType.AssistantDelta, ConversationUpdateType.Cancelled,
                ConversationUpdateType.AssistantCompleted, ConversationUpdateType.Failed })
            {
                observer(new(type, admitted.ConversationId, "late-A", "old answer must not appear",
                    ServantId: RoleId, RequestId: firstId));
                Assert.True(model.IsStreaming);
                Assert.DoesNotContain(model.Turns, t => t.MessageId == "late-A");
                Log("late." + type, "rejected", firstId);
            }
            b.TrySetResult(true); await second.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(model.IsStreaming); Assert.Empty(model.InputText);
            Assert.Contains(model.Turns, t => t.IsAssistant && t.Text == "second answer");

            model.InputText = "third";
            var third = model.SendCommand.ExecuteAsync(null); await WaitForUsers(3);
            var oldConversation = events.Last(e => e.Type == ConversationUpdateType.UserMessagePersisted);
            model.NewConversationCommand.Execute(null);
            observer(new(ConversationUpdateType.AssistantDelta, oldConversation.ConversationId, "late-C", "old conversation",
                ServantId: RoleId, RequestId: oldConversation.RequestId));
            Assert.False(model.IsStreaming); Assert.DoesNotContain(model.Turns, t => t.MessageId == "late-C");
            c.TrySetResult(true); await third.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.DoesNotContain(model.Turns, t => t.IsAssistant && t.Text == "third answer");
            Log("session.switch", "old-results-rejected"); Log("flow.completed", "passed");
        }
        finally { a.TrySetResult(true); b.TrySetResult(true); c.TrySetResult(true); }
    }

    [Fact]
    public Task User_input_wait_projects_card_blocks_new_send_and_keeps_stop_available() => RunInputChatTest(async () =>
    {
        var provider = new ScriptedProvider(QuestionTurn(), ProviderTurn.Final("answered"));
        await using var host = await TestHost.CreateAsync(provider, new RecordingTool(ToolEffect.ReadOnly), new RecordingObserver(), questions: true);
        using var model = new ConversationViewModel(host.Orchestrator, host.Settings);
        model.SetActiveServant(RoleId);
        model.InputText = "Synthetic clarification request";
        await model.SendCommand.ExecuteAsync(null);
        using var session = new ChatWebSession(model, new InputChatHost());
        var card = InputCard(session);
        Assert.Equal(3, card.GetProperty("questions").GetArrayLength());
        Assert.False(model.IsStreaming);
        Assert.True(model.StopCommand.CanExecute(null));
        Assert.Empty(model.InputText);
        model.InputText = "A new draft";
        Assert.False(model.SendCommand.CanExecute(null));
        Assert.False(model.RetryCapabilityCommand.CanExecute(null));
        Assert.False((await session.HandleCommandAsync(InputCommand(session, "chat.send"), default)).Success);
        Assert.Single(provider.Requests);
    });

    [Fact]
    public Task User_input_reply_through_bridge_continues_original_run_and_rejects_replay() => RunInputChatTest(async () =>
    {
        var provider = new ScriptedProvider(QuestionTurn(), ProviderTurn.Final("answered"));
        await using var host = await TestHost.CreateAsync(provider, new RecordingTool(ToolEffect.ReadOnly), new RecordingObserver(), questions: true);
        using var model = new ConversationViewModel(host.Orchestrator, host.Settings);
        model.SetActiveServant(RoleId);
        model.InputText = "Synthetic clarification request";
        await model.SendCommand.ExecuteAsync(null);
        using var session = new ChatWebSession(model, new InputChatHost());
        var card = InputCard(session);
        var message = InputCommand(session, "chat.input.answer", card, InputAnswers());
        var accepted = await session.HandleCommandAsync(message, default);
        Assert.True(accepted.Success);
        await AwaitInputCompletion(model);
        Assert.Equal("answered", Assert.Single(model.Turns.Where(turn => turn.IsAssistant && turn.Text == "answered")).Text);
        Assert.Equal(3, provider.Requests.Count);
        var run = await host.Runs.LoadAsync(card.GetProperty("runId").GetString()!, default);
        Assert.Equal(AgentRunStatus.Completed, run!.Snapshot.Status);
        Assert.Single(run.ResolvedInputs);
        Assert.False((await session.HandleCommandAsync(message, default)).Success);
        Assert.Equal(3, provider.Requests.Count);
    });

    [Fact]
    public Task User_input_card_survives_surface_disposal_but_new_conversation_rejects_old_reply() => RunInputChatTest(async () =>
    {
        var provider = new ScriptedProvider(QuestionTurn(), ProviderTurn.Final("answered"));
        await using var host = await TestHost.CreateAsync(provider, new RecordingTool(ToolEffect.ReadOnly), new RecordingObserver(), questions: true);
        using var model = new ConversationViewModel(host.Orchestrator, host.Settings);
        model.SetActiveServant(RoleId);
        model.InputText = "Synthetic clarification request";
        await model.SendCommand.ExecuteAsync(null);
        var oldSession = new ChatWebSession(model, new InputChatHost());
        var card = InputCard(oldSession);
        oldSession.Dispose();
        using var session = new ChatWebSession(model, new InputChatHost());
        Assert.Equal(card.GetProperty("requestId").GetString(), InputCard(session).GetProperty("requestId").GetString());
        var oldReply = InputCommand(session, "chat.input.answer", card, InputAnswers());
        model.NewConversationCommand.Execute(null);
        Assert.False((await session.HandleCommandAsync(oldReply, default)).Success);
        Assert.Single(provider.Requests);
    });

    private static Task RunInputChatTest(Func<Task> test)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
            dispatcher.BeginInvoke(new Action(async () =>
            {
                try { await test(); completion.TrySetResult(); }
                catch (Exception error) { completion.TrySetException(error); }
                finally { dispatcher.BeginInvokeShutdown(DispatcherPriority.Background); }
            }));
            Dispatcher.Run();
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return completion.Task.WaitAsync(TimeSpan.FromSeconds(15));
    }

    [Fact]
    public Task User_input_expiry_cancels_locally_without_model_or_tool_dispatch() => RunInputChatTest(async () =>
    {
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var provider = new ScriptedProvider(QuestionTurn(), ProviderTurn.Final("must not run"));
        var tool = new RecordingTool(ToolEffect.ReadOnly);
        await using var host = await TestHost.CreateAsync(provider, tool, new RecordingObserver(), questions: true, time: clock);
        using var model = new ConversationViewModel(host.Orchestrator, host.Settings, time: clock);
        model.SetActiveServant(RoleId);
        model.InputText = "Synthetic clarification request";
        await model.SendCommand.ExecuteAsync(null);
        using var session = new ChatWebSession(model, new InputChatHost());
        var card = InputCard(session);
        var oldReply = InputCommand(session, "chat.input.answer", card, InputAnswers());
        clock.Advance(TimeSpan.FromMinutes(15));
        await Task.Delay(50);
        Assert.False(model.HasPendingRun);
        Assert.Contains("过期", model.RequestStatusText);
        var run = await host.Runs.LoadAsync(card.GetProperty("runId").GetString()!, default);
        Assert.Equal(AgentRunStatus.Cancelled, run!.Snapshot.Status);
        Assert.Null(run.PendingInput);
        Assert.Null(run.Waiting);
        Assert.False((await session.HandleCommandAsync(oldReply, default)).Success);
        Assert.Single(provider.Requests);
        Assert.Empty(tool.Invocations);
    });

    [Theory]
    [InlineData("run")]
    [InlineData("request")]
    [InlineData("revision")]
    [InlineData("incomplete")]
    [InlineData("invalid-option")]
    [InlineData("multiple-single")]
    public Task User_input_invalid_reply_keeps_live_card_and_does_not_dispatch(string mismatch) => RunInputChatTest(async () =>
    {
        var provider = new ScriptedProvider(QuestionTurn(), ProviderTurn.Final("answered"));
        await using var host = await TestHost.CreateAsync(provider, new RecordingTool(ToolEffect.ReadOnly), new RecordingObserver(), questions: true);
        using var model = new ConversationViewModel(host.Orchestrator, host.Settings);
        model.SetActiveServant(RoleId);
        model.InputText = "Synthetic clarification request";
        await model.SendCommand.ExecuteAsync(null);
        using var session = new ChatWebSession(model, new InputChatHost());
        var card = InputCard(session);
        var valid = InputCommand(session, "chat.input.answer", card, InputAnswers());
        var payload = valid.Payload.EnumerateObject().ToDictionary(field => field.Name, field => field.Value.Clone());
        if (mismatch == "run") payload["runId"] = JsonSerializer.SerializeToElement("obsolete-run");
        if (mismatch == "request") payload["requestId"] = JsonSerializer.SerializeToElement("obsolete-request");
        if (mismatch == "revision") payload["revision"] = JsonSerializer.SerializeToElement(card.GetProperty("revision").GetInt64() - 1);
        if (mismatch is "incomplete" or "invalid-option" or "multiple-single")
        {
            var answers = InputAnswers();
            if (mismatch == "incomplete") answers = [answers[0]];
            else answers[0] = new { questionId = "single", selectedOptionIndices = mismatch == "invalid-option" ? new[] { 9 } : new[] { 0, 1 }, text = (string?)null };
            payload["answers"] = JsonSerializer.SerializeToElement(answers);
        }
        var rejected = await session.HandleCommandAsync(valid with { Payload = JsonSerializer.SerializeToElement(payload) }, default);
        Assert.False(rejected.Success);
        Assert.Equal(card.GetProperty("requestId").GetString(), InputCard(session).GetProperty("requestId").GetString());
        Assert.Single(provider.Requests);
        Assert.Empty((await host.Runs.LoadAsync(card.GetProperty("runId").GetString()!, default))!.ResolvedInputs);
    });

    [Theory]
    [InlineData("new")]
    [InlineData("role")]
    [InlineData("project")]
    [InlineData("history")]
    [InlineData("dispose")]
    public Task User_input_session_invalidation_rejects_old_answer(string change) => RunInputChatTest(async () =>
    {
        var provider = new ScriptedProvider(QuestionTurn(), ProviderTurn.Final("must not run"));
        await using var host = await TestHost.CreateAsync(provider, new RecordingTool(ToolEffect.ReadOnly), new RecordingObserver(), questions: true);
        using var model = new ConversationViewModel(host.Orchestrator, host.Settings);
        model.SetActiveServant(RoleId);
        model.InputText = "Synthetic clarification request";
        await model.SendCommand.ExecuteAsync(null);
        using var session = new ChatWebSession(model, new InputChatHost());
        var oldReply = InputCommand(session, "chat.input.answer", InputCard(session), InputAnswers());
        switch (change)
        {
            case "new": model.NewConversationCommand.Execute(null); break;
            case "role": model.SetActiveServant("other-role"); break;
            case "project": Assert.True(model.SessionContext.TrySetProject("other-project", "Synthetic project")); break;
            case "history": model.SetActiveConversation(model.CurrentConversationId); break;
            case "dispose": model.Dispose(); break;
        }
        Assert.False((await session.HandleCommandAsync(oldReply, default)).Success);
        Assert.False(model.HasPendingRun);
        Assert.Single(provider.Requests);
    });

    [Fact]
    public Task User_input_second_question_in_same_run_rejects_first_card() => RunInputChatTest(async () =>
    {
        var provider = new ScriptedProvider(QuestionTurn(), QuestionTurn("ask-second"), ProviderTurn.Final("answered"));
        await using var host = await TestHost.CreateAsync(provider, new RecordingTool(ToolEffect.ReadOnly), new RecordingObserver(), questions: true);
        using var model = new ConversationViewModel(host.Orchestrator, host.Settings);
        model.SetActiveServant(RoleId);
        model.InputText = "Synthetic clarification request";
        await model.SendCommand.ExecuteAsync(null);
        using var session = new ChatWebSession(model, new InputChatHost());
        var first = InputCard(session);
        var oldReply = InputCommand(session, "chat.input.answer", first, InputAnswers());
        Assert.True((await session.HandleCommandAsync(oldReply, default)).Success);
        await AwaitInputCompletion(model);
        var second = InputCard(session);
        Assert.Equal(first.GetProperty("runId").GetString(), second.GetProperty("runId").GetString());
        Assert.NotEqual(first.GetProperty("requestId").GetString(), second.GetProperty("requestId").GetString());
        Assert.False((await session.HandleCommandAsync(oldReply, default)).Success);
        Assert.Equal(2, provider.Requests.Count);
        Assert.True((await session.HandleCommandAsync(InputCommand(session, "chat.input.answer", second, InputAnswers()), default)).Success);
        await AwaitInputCompletion(model);
        Assert.True(model.Turns.Any(turn => turn.IsAssistant && turn.Text == "answered"),
            await host.SafeRunErrorsAsync(new(model.CurrentConversationId!, RoleId, null)));
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task User_input_unicode_answer_at_broker_limit_survives_bridge_byte_measurement(bool supplementary) => RunInputChatTest(async () =>
    {
        var provider = new ScriptedProvider(QuestionTurn(), ProviderTurn.Final("answered"));
        // The legal 12,000-unit answer also needs to fit the selected model's context window.
        await using var host = await TestHost.CreateAsync(provider, new RecordingTool(ToolEffect.ReadOnly), new RecordingObserver(),
            settings: TestSettingsStore.Create(131072), questions: true);
        using var model = new ConversationViewModel(host.Orchestrator, host.Settings);
        model.SetActiveServant(RoleId);
        model.InputText = "Synthetic clarification request";
        await model.SendCommand.ExecuteAsync(null);
        using var session = new ChatWebSession(model, new InputChatHost());
        var card = InputCard(session);
        // Three selected labels + ten-unit supplement + 11,987 free-text units = 12,000.
        var literal = supplementary ? string.Concat(Enumerable.Repeat("😀", 5993)) + "中" : new string('中', 11987);
        var command = InputCommand(session, "chat.input.answer", card, InputAnswers());
        // JSON.stringify emits both CJK and supplementary Unicode literally.
        using var document = JsonDocument.Parse(command.Payload.GetRawText().Replace("Synthetic detail", literal, StringComparison.Ordinal));
        command = command with { Payload = document.RootElement.Clone() };
        Assert.True((await session.HandleCommandAsync(command, default)).Success);
        await AwaitInputCompletion(model);
        var checkpoint = (await host.Runs.LoadAsync(card.GetProperty("runId").GetString()!, default))!;
        Assert.True(checkpoint.Snapshot.Status == AgentRunStatus.Completed, checkpoint.Snapshot.ErrorCode);
        Assert.Equal(3, provider.Requests.Count);
    });

    [Theory]
    [InlineData(11997, "ascii", false)]
    [InlineData(11998, "ascii", false)]
    [InlineData(12000, "ascii", false)]
    [InlineData(12000, "supplementary", false)]
    [InlineData(12000, "ascii", true)]
    [InlineData(12000, "supplementary", true)]
    [InlineData(12000, "whitespace", true)]
    [InlineData(12000, "padded", true)]
    [InlineData(12000, "escaped", false)]
    public Task User_input_single_answer_boundary_publishes_complete_history_and_releases_chat(
        int totalLength, string textKind, bool withSelections) => RunInputChatTest(async () =>
    {
        const string question = "Provide a synthetic boundary answer";
        var meter = new RecordingTokenMeter();
        var provider = new ScriptedProvider(ProviderTurn.Call("user.ask", "boundary-ask", JsonSerializer.Serialize(new
        {
            questions = new[] { new { id = "boundary", question, options = new[] { "A", "B" }, allowMultiple = true } }
        })), ProviderTurn.Final("boundary accepted"), ProviderTurn.Final("follow-up after boundary"));
        // The next turn retains both the protected tool reply and semantic history. Their JSON
        // representation costs more than the initial reply, especially for supplementary Unicode.
        var contextWindow = textKind == "supplementary" ? 262144 : 131072;
        await using var host = await TestHost.CreateAsync(provider, new RecordingTool(ToolEffect.ReadOnly), new RecordingObserver(),
            settings: TestSettingsStore.Create(contextWindow), questions: true, tokenMeter: meter);
        using var model = new ConversationViewModel(host.Orchestrator, host.Settings);
        model.SetActiveServant(RoleId);
        model.InputText = "Synthetic boundary request";
        await model.SendCommand.ExecuteAsync(null);
        using var session = new ChatWebSession(model, new InputChatHost());
        var card = InputCard(session);
        var textLength = totalLength - (withSelections ? 2 : 0);
        var text = textKind switch
        {
            "supplementary" => string.Concat(Enumerable.Repeat("😀", textLength / 2)),
            "whitespace" => new string(' ', textLength),
            "padded" => " " + new string('x', textLength - 2) + " ",
            "escaped" => new string('<', textLength),
            _ => new string('x', textLength)
        };
        Assert.Equal(textLength, text.Length);
        var command = InputCommand(session, "chat.input.answer", card,
            [new { questionId = "boundary", selectedOptionIndices = withSelections ? new[] { 0, 1 } : Array.Empty<int>(), text }]);
        // Match JSON.stringify, which emits supplementary characters literally rather than \u pairs.
        using var literalPayload = JsonDocument.Parse(command.Payload.GetRawText().Replace("\\uD83D\\uDE00", "😀", StringComparison.OrdinalIgnoreCase));
        command = command with { Payload = literalPayload.RootElement.Clone() };
        Assert.True((await session.HandleCommandAsync(command, default)).Success);
        await AwaitInputCompletion(model);

        Assert.False(model.HasPendingRun);
        Assert.Null(model.PendingInput);
        Assert.False(model.IsInputSubmitting);
        Assert.Contains(model.Turns, turn => turn.IsAssistant && turn.Text == "boundary accepted");
        var checkpoint = (await host.Runs.LoadAsync(card.GetProperty("runId").GetString()!, default))!;
        Assert.Equal(AgentRunStatus.Completed, checkpoint.Snapshot.Status);
        var resolved = Assert.Single(checkpoint.ResolvedInputs);
        Assert.Equal(text, Assert.Single(resolved.Reply.Answers).Text);
        var messages = host.Conversations.LoadMessages(model.CurrentConversationId, RoleId);
        Assert.Equal(question, messages[1].Text);
        var semanticAnswers = messages.Skip(2).Take(messages.Count - 3).ToArray();
        Assert.All(semanticAnswers, answer =>
        {
            Assert.Equal(ChatMessageRole.User, answer.Role);
            Assert.InRange(answer.Text.Length, 1, 12_000);
            Assert.False(string.IsNullOrWhiteSpace(answer.Text));
            Assert.True(new UTF8Encoding(false, true).GetByteCount(answer.Text) > 0);
        });
        Assert.Equal(text, string.Concat(semanticAnswers.Where(answer => answer.Text.StartsWith("补充：", StringComparison.Ordinal))
            .Select(answer => answer.Text.StartsWith("补充：“", StringComparison.Ordinal) ? answer.Text[4..^1] : answer.Text[3..])));
        if (withSelections) Assert.Contains(semanticAnswers, answer => answer.Text.Contains("选择：A、B", StringComparison.Ordinal));
        Assert.Equal("boundary accepted", messages[^1].Text);
        Assert.NotNull(host.ReopenDeliveries().Publish(checkpoint.Snapshot.Identity.Scope, checkpoint.FinalDeliveryId!));
        Assert.Equal(messages, host.Conversations.LoadMessages(model.CurrentConversationId, RoleId));
        Assert.False((await session.HandleCommandAsync(command, default)).Success);
        Assert.Equal(3, provider.Requests.Count);
        model.InputText = "Ready for another message";
        Assert.True(model.CanSend);
        await model.SendCommand.ExecuteAsync(null);
        Assert.True(model.Turns.Any(turn => turn.IsAssistant && turn.Text == "follow-up after boundary"),
            $"Follow-up failed: {model.ErrorText}; maxInputTokens={meter.Maximum}; {await host.SafeRunErrorsAsync(checkpoint.Snapshot.Identity.Scope)}");
        Assert.Equal(5, provider.Requests.Count);
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Native_optional_context_reserves_complete_history_before_selecting_background(bool questions)
    {
        var provider = new ScriptedProvider(ProviderTurn.Final("first answer"), ProviderTurn.Final("history retained"));
        var meter = new CharacterTokenMeter();
        await using var host = await TestHost.CreateAsync(provider, new RecordingTool(ToolEffect.ReadOnly), new RecordingObserver(),
            settings: TestSettingsStore.Create(131072), tokenMeter: meter, questions: questions);
        var original = new string('h', 5000);
        Assert.Equal(ConversationSendStatus.Completed, (await host.Orchestrator.SendAsync(RoleId, original, default, new(ProjectId))).Status);
        var initialTokens = provider.Requests[0].ModelMessages.Sum(message => message.Content.Length);
        host.Settings.Save(TestSettingsStore.Create(initialTokens + 1600).Load());
        var attachments = Enumerable.Range(0, 8).Select(index => new string('a', 159) + index).ToArray();
        var result = await host.Orchestrator.SendAsync(RoleId, "Continue the synthetic history", default,
            new(ProjectId, new string('p', 160), attachments));

        Assert.True(result.Status == ConversationSendStatus.Completed,
            $"{result.Status}: {result.SafeError}; {await host.SafeRunErrorsAsync(new(result.ConversationId, RoleId, ProjectId))}");
        Assert.Equal(4, provider.Requests.Count);
        var request = provider.Requests[^1];
        Assert.Contains(request.ModelMessages, message => message.Content.Contains(original, StringComparison.Ordinal));
        Assert.DoesNotContain(request.ModelMessages, message => message.Content.Contains(attachments[^1], StringComparison.Ordinal));
        Assert.Null(host.ContextStore); // The optional reduction must work without a summary service.
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task User_input_over_boundary_keeps_card_and_does_not_dispatch(bool withSelection) => RunInputChatTest(async () =>
    {
        var provider = new ScriptedProvider(ProviderTurn.Call("user.ask", "boundary-ask", """
            {"questions":[{"id":"boundary","question":"Provide detail","options":["A"]}]}
            """), ProviderTurn.Final("must not run"));
        await using var host = await TestHost.CreateAsync(provider, new RecordingTool(ToolEffect.ReadOnly), new RecordingObserver(), questions: true);
        using var model = new ConversationViewModel(host.Orchestrator, host.Settings);
        model.SetActiveServant(RoleId);
        model.InputText = "Synthetic boundary request";
        await model.SendCommand.ExecuteAsync(null);
        using var session = new ChatWebSession(model, new InputChatHost());
        var card = InputCard(session);
        var command = InputCommand(session, "chat.input.answer", card,
            [new { questionId = "boundary", selectedOptionIndices = withSelection ? new[] { 0 } : Array.Empty<int>(),
                text = new string('x', withSelection ? 12000 : 12001) }]);
        Assert.False((await session.HandleCommandAsync(command, default)).Success);
        Assert.Equal(card.GetProperty("requestId").GetString(), InputCard(session).GetProperty("requestId").GetString());
        Assert.True(model.HasPendingRun);
        Assert.False(model.IsInputSubmitting);
        Assert.Single(provider.Requests);
        Assert.Empty((await host.Runs.LoadAsync(card.GetProperty("runId").GetString()!, default))!.ResolvedInputs);
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task User_input_final_delivery_failure_clears_terminal_card_and_allows_fresh_chat(bool unexpectedError) => RunInputChatTest(async () =>
    {
        var provider = new ScriptedProvider(QuestionTurn(), ProviderTurn.Final("rejected final"), ProviderTurn.Final("fresh answer"));
        // This lifecycle fixture retains the completed question evidence after delivery fails.
        // Leave room for that history and the charged phase-control schema; budget gates have separate coverage.
        await using var host = await TestHost.CreateAsync(provider, new RecordingTool(ToolEffect.ReadOnly), new RecordingObserver(),
            settings: TestSettingsStore.Create(32768), questions: true,
            deliveryDecorator: inner => new FailOnceFinalDeliveryStore(inner, unexpectedError));
        using var model = new ConversationViewModel(host.Orchestrator, host.Settings);
        model.SetActiveServant(RoleId);
        model.InputText = "Synthetic clarification request";
        await model.SendCommand.ExecuteAsync(null);
        using var session = new ChatWebSession(model, new InputChatHost());
        var card = InputCard(session);
        var command = InputCommand(session, "chat.input.answer", card, InputAnswers());
        Assert.True((await session.HandleCommandAsync(command, default)).Success);
        await AwaitInputCompletion(model);

        var checkpoint = (await host.Runs.LoadAsync(card.GetProperty("runId").GetString()!, default))!;
        Assert.Equal(AgentRunStatus.Completed, checkpoint.Snapshot.Status);
        Assert.Null(await host.Orchestrator.GetAgentInteractionAsync(checkpoint.Snapshot.Identity.RunId, checkpoint.Snapshot.Identity.Scope, default));
        Assert.False(model.HasPendingRun);
        Assert.Null(model.PendingInput);
        Assert.False(model.IsInputSubmitting);
        Assert.False(string.IsNullOrWhiteSpace(model.ErrorText));
        Assert.DoesNotContain(model.Turns, turn => turn.IsAssistant && turn.Text == "rejected final");
        Assert.False((await session.HandleCommandAsync(command, default)).Success);
        Assert.Equal(3, provider.Requests.Count);
        model.InputText = "A fresh message";
        Assert.True(model.CanSend);
        await model.SendCommand.ExecuteAsync(null);
        Assert.True(model.Turns.Any(turn => turn.IsAssistant && turn.Text == "fresh answer"),
            await host.SafeRunErrorsAsync(new(model.CurrentConversationId!, RoleId, null)));
        Assert.Equal(5, provider.Requests.Count);
    });

    [Fact]
    public Task User_input_stale_reply_refreshes_the_current_live_card_without_dispatch() => RunInputChatTest(async () =>
    {
        var provider = new ScriptedProvider(QuestionTurn(), QuestionTurn("new-question"), ProviderTurn.Final("answered current question"));
        await using var host = await TestHost.CreateAsync(provider, new RecordingTool(ToolEffect.ReadOnly), new RecordingObserver(), questions: true);
        using var model = new ConversationViewModel(host.Orchestrator, host.Settings);
        model.SetActiveServant(RoleId);
        model.InputText = "Synthetic clarification request";
        await model.SendCommand.ExecuteAsync(null);
        using var session = new ChatWebSession(model, new InputChatHost());
        var oldCard = InputCard(session);
        var binding = model.PendingInput!.Binding;
        // Another host caller consumes the first reply while this view still displays its old card.
        var next = await host.Orchestrator.ResumeAgentInputAsync(new(binding.Identity.RunId, binding.RequestId, binding.WaitingRevision,
            [new("single", [1], null), new("multiple", [0, 1], "Supplement"), new("free", [], "Synthetic detail")]),
            binding.Identity.Scope, default);
        Assert.Equal(ConversationSendStatus.WaitingUserInput, next.Status);
        var live = (await host.Orchestrator.GetAgentInteractionAsync(binding.Identity.RunId, binding.Identity.Scope, default))!;
        Assert.NotEqual(binding.RequestId, live.Input!.Binding.RequestId);

        var stale = InputCommand(session, "chat.input.answer", oldCard, InputAnswers());
        Assert.True((await session.HandleCommandAsync(stale, default)).Success);
        await AwaitInputCompletion(model);
        Assert.True(model.HasPendingRun);
        Assert.False(model.IsInputSubmitting);
        Assert.Equal(live.Input.Binding.RequestId, InputCard(session).GetProperty("requestId").GetString());
        Assert.Equal(2, provider.Requests.Count);
        Assert.False((await session.HandleCommandAsync(stale, default)).Success);
        Assert.True((await session.HandleCommandAsync(InputCommand(session, "chat.input.answer", InputCard(session), InputAnswers()), default)).Success);
        await AwaitInputCompletion(model);
        Assert.False(model.HasPendingRun);
        Assert.Contains(model.Turns, turn => turn.IsAssistant && turn.Text == "answered current question");
        Assert.Equal(4, provider.Requests.Count);
    });

    [Fact]
    public Task User_input_cancel_clears_wait_without_dispatch_and_accepts_new_message() => RunInputChatTest(async () =>
    {
        var provider = new ScriptedProvider(QuestionTurn(), ProviderTurn.Final("fresh answer"));
        await using var host = await TestHost.CreateAsync(provider, new RecordingTool(ToolEffect.ReadOnly), new RecordingObserver(), questions: true);
        using var model = new ConversationViewModel(host.Orchestrator, host.Settings);
        model.SetActiveServant(RoleId);
        model.InputText = "Synthetic clarification request";
        await model.SendCommand.ExecuteAsync(null);
        using var session = new ChatWebSession(model, new InputChatHost());
        var card = InputCard(session);
        Assert.True((await session.HandleCommandAsync(InputCommand(session, "chat.input.cancel", card), default)).Success);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (model.HasPendingRun) await Task.Delay(10, deadline.Token);
        Assert.Single(provider.Requests);
        Assert.False((await session.HandleCommandAsync(InputCommand(session, "chat.input.answer", card, InputAnswers()), default)).Success);
        Assert.Equal(AgentRunStatus.Cancelled, (await host.Runs.LoadAsync(card.GetProperty("runId").GetString()!, default))!.Snapshot.Status);
        model.InputText = "A fresh message";
        await model.SendCommand.ExecuteAsync(null);
        Assert.Contains(model.Turns, turn => turn.IsAssistant && turn.Text == "fresh answer");
        Assert.Equal(3, provider.Requests.Count);
    });

    private static ProviderTurn QuestionTurn(string callId = "ask-fixture") => ProviderTurn.Call("user.ask", callId, """
        {"questions":[{"id":"single","question":"Choose one","options":["A","B"]},
        {"id":"multiple","question":"Choose several","options":["C","D"],"allowMultiple":true},
        {"id":"free","question":"Provide detail"}]}
        """);
    private static object[] InputAnswers() =>
    [
        new { questionId = "single", selectedOptionIndices = new[] { 1 }, text = (string?)null },
        new { questionId = "multiple", selectedOptionIndices = new[] { 0, 1 }, text = "Supplement" },
        new { questionId = "free", selectedOptionIndices = Array.Empty<int>(), text = "Synthetic detail" },
    ];
    private static JsonElement InputCard(ChatWebSession session)
    {
        var snapshot = JsonSerializer.SerializeToElement(session.ReadSnapshot(), new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.True(snapshot.GetProperty("conversation").TryGetProperty("pendingInput", out var card), "Waiting user input must be projected to Chat.");
        Assert.Equal(JsonValueKind.Object, card.ValueKind);
        return card;
    }
    private static WebSurfaceMessage InputCommand(ChatWebSession session, string type, JsonElement? card = null, object[]? answers = null)
    {
        var identity = session.ReadSnapshot().Identity;
        var payload = new Dictionary<string, object?>
        {
            ["sessionId"] = identity.SessionId, ["servantId"] = identity.ServantId, ["conversationId"] = identity.ConversationId,
        };
        if (card is { } input)
        {
            payload["runId"] = input.GetProperty("runId").GetString();
            payload["requestId"] = input.GetProperty("requestId").GetString();
            payload["revision"] = input.GetProperty("revision").GetInt64();
        }
        if (answers is not null) payload["answers"] = answers;
        return new(type, Guid.NewGuid().ToString("N"), JsonSerializer.SerializeToElement(payload,
            new JsonSerializerOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));
    }
    private static async Task AwaitInputCompletion(ConversationViewModel model)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (model.IsStreaming) await Task.Delay(10, deadline.Token);
    }
    private sealed class InputChatHost : IChatWebHostActions
    {
        public event EventHandler? Changed { add { } remove { } }
        public ChatWebHostPresentation ReadPresentation() => new("Synthetic role", null, [], []);
        public ValueTask<WebSurfaceCommandResult> HandleAsync(ChatWebHostRequest request, CancellationToken token) =>
            ValueTask.FromResult(new WebSurfaceCommandResult(false));
    }

    private sealed class TestHost : IAsyncDisposable
    {
        private const string ActiveConversationKey = "LastActiveConversationId:" + RoleId;
        private readonly string directory = Path.Combine(Path.GetTempPath(), "fgo-native-conversation-" + Guid.NewGuid().ToString("N"));
        private readonly SyntheticProtector protector = new();
        private readonly PluginRuntime plugins;
        private readonly NativeConversationRuntime nativeRuntime;
        private readonly DateTimeOffset now = new(2026, 10, 6, 0, 0, 0, TimeSpan.Zero);

        private TestHost(IChatProvider provider, IToolProvider? tool, RecordingObserver observer,
            TestSettingsStore? settings, bool enableSummary, IConversationSummarizer? summarizer,
            IRequestTokenMeter? tokenMeter, IAgentContextProvider? context, ILogger<ConversationOrchestrator>? logger, bool questions, TimeProvider? time,
            Func<IAgentFinalDeliveryStore, IAgentFinalDeliveryStore>? deliveryDecorator,
            IConversationContentResolver? contentResolver, bool useNative, bool skills, IReadOnlyList<IToolProvider>? extraTools)
        {
            Directory.CreateDirectory(directory);
            Database = new(Path.Combine(directory, "runtime.db"), pooling: false);
            new RuntimeDatabaseMigrator(Database).Migrate();
            Conversations = new(Database);
            var clock = time ?? TimeProvider.System;
            Runs = new(Database, protector, clock);
            Deliveries = new(Database, protector, Runs);
            Settings = settings ?? TestSettingsStore.Create();

            var plugin = new TestPlugin(tool, observer, context, extraTools);
            var catalog = skills ? PluginCatalog.Create([plugin, new FgoPet.Plugin.Skills.SkillsPlugin()]) : PluginCatalog.Create([plugin]);
            plugins = new(catalog);
            nativeRuntime = new(Runs, new ToolRegistry(catalog, plugins), new NativeAgentExtensions(
                Skills: skills ? new SkillRegistry(catalog, plugins, new ToolRegistry(catalog, plugins)) : null, Context: new(catalog, plugins),
                Questions: questions ? new UserInputBroker(clock) : null), clock);
            var activation = plugins.StartAsync(default).GetAwaiter().GetResult();
            if (!activation.Succeeded) throw new InvalidOperationException("TEST_PLUGIN_ACTIVATION_FAILED");
            var capabilities = new ConversationCapabilityRouter(catalog, plugins);
            ContextStore = enableSummary ? new SqliteConversationContextStore(Database) : null;
            var summaries = ContextStore is null || summarizer is null ? null
                : new ConversationSummaryService(ContextStore, summarizer, tokenMeter ?? new RequestTokenMeter(), TimeProvider.System);
            Orchestrator = new(new TestProviderResolver(provider), contentResolver ?? new TestContentResolver(), Conversations,
                new PromptComposer(tokenMeter), clock, Settings, summaries, capabilities,
                tokenMeter: tokenMeter, contextStore: ContextStore, nativeRuntime: useNative ? nativeRuntime : null,
                agentRuns: Runs, finalDeliveries: deliveryDecorator?.Invoke(Deliveries) ?? Deliveries, logger: logger);
        }

        public RuntimeDatabase Database { get; }
        public SqliteConversationRepository Conversations { get; }
        public SqliteAgentRunStore Runs { get; }
        public SqliteAgentFinalDeliveryStore Deliveries { get; }
        public SqliteAgentFinalDeliveryStore ReopenDeliveries() =>
            new(new RuntimeDatabase(Database.DatabasePath, pooling: false), protector, Runs);
        public async Task<string> SafeRunErrorsAsync(ToolScope scope)
        {
            var roots = Conversations.LoadMessages(scope.ConversationId, scope.RoleId)
                .Where(message => message.Role == ChatMessageRole.User).Select(message => message.MessageId).ToArray();
            var runs = await Runs.LoadConversationRunsAsync(scope, roots, default);
            return string.Join(",", runs.Select(run => $"{run.Snapshot.Status}:{run.Snapshot.ErrorCode}"));
        }
        public TestSettingsStore Settings { get; }
        public ConversationOrchestrator Orchestrator { get; }
        public SqliteConversationContextStore? ContextStore { get; }

        public static Task<TestHost> CreateAsync(IChatProvider provider, IToolProvider? tool,
            RecordingObserver observer, TestSettingsStore? settings = null, bool enableSummary = false,
            IConversationSummarizer? summarizer = null, IRequestTokenMeter? tokenMeter = null, IAgentContextProvider? context = null,
            ILogger<ConversationOrchestrator>? logger = null, bool questions = false, TimeProvider? time = null,
            Func<IAgentFinalDeliveryStore, IAgentFinalDeliveryStore>? deliveryDecorator = null,
            IConversationContentResolver? contentResolver = null, bool useNative = true, bool skills = false,
            IReadOnlyList<IToolProvider>? extraTools = null)
        {
            return Task.FromResult(new TestHost(provider, tool, observer, settings, enableSummary, summarizer, tokenMeter, context, logger, questions, time, deliveryDecorator, contentResolver, useNative, skills, extraTools));
        }

        public long CountRows(string table)
        {
            Assert.Contains(table, new[] { "agent_final_deliveries" }, StringComparer.Ordinal);
            using var connection = Database.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name=$table";
            command.Parameters.AddWithValue("$table", table);
            if ((long)command.ExecuteScalar()! == 0) return 0;
            command.CommandText = $"SELECT COUNT(*) FROM {table}";
            return (long)command.ExecuteScalar()!;
        }

        public void SeedHistoryForCompaction()
        {
            var context = new ContentContextKey(RoleId, "test-package", "1.0.0", "default", "persona-v1", "knowledge-v1");
            Conversations.CreateConversation("summary-conversation", RoleId, context, now, ProjectId);
            Conversations.Append(new("user-old-1", "summary-conversation", RoleId, ChatMessageRole.User,
                "旧目标：" + new string('甲', 4500), ChatMessageStatus.Completed, now, context, 1));
            Conversations.Append(new("assistant-old-1", "summary-conversation", RoleId, ChatMessageRole.Assistant,
                "旧答复：" + new string('乙', 4500), ChatMessageStatus.Completed, now.AddSeconds(1), context, 2));
            for (var turn = 2; turn <= 3; turn++)
            {
                var userId = $"user-old-{turn}";
                var assistantId = $"assistant-old-{turn}";
                var sequence = turn * 2 - 1;
                Conversations.Append(new(userId, "summary-conversation", RoleId, ChatMessageRole.User,
                    $"最近请求 {turn}：" + new string('丙', 100), ChatMessageStatus.Completed, now.AddMinutes(turn), context, sequence));
                Conversations.Append(new(assistantId, "summary-conversation", RoleId, ChatMessageRole.Assistant,
                    $"最近答复 {turn}：" + new string('丁', 100), ChatMessageStatus.Completed, now.AddMinutes(turn).AddSeconds(1), context, sequence + 1));
            }
            Conversations.WriteState(ActiveConversationKey, "summary-conversation", now);
        }

        public async ValueTask DisposeAsync()
        {
            await nativeRuntime.DisposeAsync();
            await plugins.DisposeAsync();
            protector.Dispose();
            foreach (var suffix in new[] { "runtime.db", "runtime.db-wal", "runtime.db-shm" })
            {
                var path = Path.Combine(directory, suffix);
                if (File.Exists(path)) File.Delete(path);
            }
            if (Directory.Exists(directory)) Directory.Delete(directory);
        }

        private sealed class TestProviderResolver(IChatProvider provider) : IChatProviderResolver
        {
            public IChatProvider Resolve() => provider;
            public IChatProvider Resolve(ModelConnectionSettings settings) => provider;
        }

        private sealed class TestContentResolver : IConversationContentResolver
        {
            public Task<ContentBinding> ResolveAsync(string servantId, CancellationToken cancellationToken)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var context = new ContentContextKey(servantId, "test-package", "1.0.0", "default", "persona-v1", "knowledge-v1");
                return Task.FromResult(new ContentBinding(context, null, [], [], new string('a', 64), new string('b', 64)));
            }
        }

        private sealed class TestPlugin(IToolProvider? tool, RecordingObserver observer, IAgentContextProvider? context,
            IReadOnlyList<IToolProvider>? extraTools) : IFgoPetPlugin
        {
            public PluginManifest Manifest { get; } = new("fixture.native-conversation", "1.0.0", 1, []);
            public PluginContributions Contributions { get; } = PluginContributions.Empty with
            {
                Tools = [.. (tool is null ? Array.Empty<IToolProvider>() : [tool]), .. (extraTools ?? [])],
                PostTurnObservers = [observer],
                AgentContexts = context is null ? [] : [context],
            };
            public ValueTask StartAsync(CancellationToken stoppingToken) => ValueTask.CompletedTask;
            public ValueTask StopAsync(CancellationToken cancellationToken) => ValueTask.CompletedTask;
            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }

    private sealed class FailOnceFinalDeliveryStore(IAgentFinalDeliveryStore inner, bool unexpectedError) : IAgentFinalDeliveryStore
    {
        private bool failed;
        public bool TryAccept(AcceptedAgentFinal accepted)
        {
            if (failed) return inner.TryAccept(accepted);
            failed = true;
            if (unexpectedError) throw new IOException("SYNTHETIC_FINAL_FAILURE");
            return false;
        }
        public AgentFinalDelivery? Publish(ToolScope scope, string deliveryId) => inner.Publish(scope, deliveryId);
        public IReadOnlyList<AcceptedAgentFinal> ReadPending(int limit = 100) => inner.ReadPending(limit);
        public bool TryClaimObservers(ToolScope scope, string deliveryId) => inner.TryClaimObservers(scope, deliveryId);
    }

    private sealed class ContextLogger : ILogger<ConversationOrchestrator>
    {
        public List<string> Entries { get; } = [];
        public List<Dictionary<string, object?>> Dispatches { get; } = [];
        public bool IsEnabled(LogLevel logLevel) => true;
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            Entries.Add(formatter(state, exception));
            if (eventId.Id == 4100 && state is IEnumerable<KeyValuePair<string, object?>> fields)
            {
                var values = fields.ToDictionary(pair => pair.Key, pair => pair.Value);
                if (Equals(values["Phase"], "dispatch")) Dispatches.Add(values);
            }
        }
    }

    private sealed class ChangingContext : IAgentContextProvider
    {
        public string Id => "confirmed-memory";
        public bool ProvidesTrustedInstructions => false;
        public int Count { get; private set; }
        public ValueTask<IReadOnlyList<AgentContextBlock>> BuildAsync(AgentContextRequest request, CancellationToken token) =>
            ValueTask.FromResult<IReadOnlyList<AgentContextBlock>>([new("records", AgentContextKind.Data,
                $"revision={++Count};" + new string('m', 2000), 100)]);
    }

    private sealed class TestSettingsStore(DialogueSettings initial) : IDialogueSettingsStore
    {
        private DialogueSettings current = initial;
        public DialogueSettings Load() => current;
        public void Save(DialogueSettings settings) => current = settings;
        public static TestSettingsStore Create(int contextWindow = 8192) => new(DialogueSettings.Defaults with
        {
            ModelConnection = new ModelConnectionSettings("fixture", "https://fixture.invalid/v1", "fixture-model",
                contextWindowOverride: contextWindow, maxOutputTokens: 512),
        });
    }

    private sealed class RecordingTool(ToolEffect effect, string value = "tool observation") : IToolProvider
    {
        private readonly ConcurrentQueue<ToolInvocation> invocations = new();
        public ToolDescriptor Descriptor { get; } = new("fixture.read", "Return one synthetic observation.",
            """{"type":"object","properties":{},"additionalProperties":false}""", effect);
        public IReadOnlyList<ToolInvocation> Invocations => invocations.ToArray();
        public ValueTask<ToolResult> InvokeAsync(ToolInvocation invocation, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            invocations.Enqueue(invocation);
            var result = new ToolResult(true, System.Text.Json.JsonSerializer.SerializeToElement(new { value }));
            return ValueTask.FromResult(effect == ToolEffect.Command
                ? result with { ExecutionState = ToolExecutionState.Committed }
                : result);
        }
    }

    private sealed class RecordingObserver : IPostTurnObserver
    {
        private readonly ConcurrentQueue<ConversationCompletedTurn> turns = new();
        public IReadOnlyList<ConversationCompletedTurn> Turns => turns.ToArray();
        public void Observe(ConversationCompletedTurn turn) => turns.Enqueue(turn);
    }

    private sealed class ScriptedProvider(params ProviderTurn[] turns) : IChatProvider
    {
        private readonly ConcurrentQueue<ProviderTurn> script = new(turns);
        private readonly ConcurrentQueue<ChatRequest> requests = new();
        private ProviderTurn? pendingFinal;
        private string? pendingFinishId;
        private readonly TaskCompletionSource<bool> firstRequestReceived = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public string ProviderId => "fixture";
        public string ModelId => "fixture-model";
        public IReadOnlyList<ChatRequest> Requests => requests.ToArray();
        // Legacy successful scripts provide visible text. Native fixtures serialize it in the
        // same response; malformed-output tests opt out so validation sees the exact raw bytes.
        public bool RawFinalResponses { get; init; }
        // Scripted answers stay queued while an explicit, charged control request ends tools.
        // Negative protocol tests disable this convenience and supply the exact malformed turn.
        public bool AutoFinalize { get; init; } = true;
        private static string OutputEnvelope(string text, ChatRequest request)
        {
            var user = request.ModelMessages.LastOrDefault(message => message.Role == ModelMessageRole.User)?.Content ?? "";
            try { user = System.Xml.Linq.XElement.Parse(user).Value; } catch (System.Xml.XmlException) { }
            if (!user.Contains(text, StringComparison.Ordinal))
                try
                {
                    using var json = JsonDocument.Parse(text);
                    if (json.RootElement.ValueKind == JsonValueKind.Object
                        && json.RootElement.TryGetProperty("text", out var body) && body.ValueKind == JsonValueKind.String
                        && json.RootElement.TryGetProperty("emotion", out var emotion) && emotion.ValueKind == JsonValueKind.String)
                        return text;
                }
                catch (JsonException) { }
            return JsonSerializer.Serialize(new { text, emotion = "neutral" });
        }
        public Action<ChatRequest>? RequestReceived { get; set; }
        public TaskCompletionSource<bool> FirstRequestReceived => firstRequestReceived;
        public Task<IReadOnlyList<ProviderModel>> ListModelsAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<ProviderModel>>([new ProviderModel(ModelId)]);

        public async IAsyncEnumerable<ChatStreamChunk> StreamAsync(ChatRequest request,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
        {
            requests.Enqueue(request);
            RequestReceived?.Invoke(request);
            firstRequestReceived.TrySetResult(true);
            var resumedFinal = pendingFinal is not null && request.IsAgentRequest && request.Tools is null;
            if (!resumedFinal) { pendingFinal = null; pendingFinishId = null; }
            var handoff = !resumedFinal && AutoFinalize && request.IsAgentRequest && request.Tools?.Any(tool =>
                tool.Name == ModelToolNameMap.GetWireName("response.finalize")) == true
                && script.TryPeek(out var pending) && pending.Chunks.LastOrDefault()?.FinishReason == "stop";
            ProviderTurn? turn;
            if (resumedFinal) { turn = pendingFinal; pendingFinal = null; pendingFinishId = null; }
            else if (handoff)
            {
                script.TryDequeue(out pendingFinal);
                pendingFinishId = "finish-" + Guid.NewGuid().ToString("N");
                turn = ProviderTurn.Call("response.finalize", pendingFinishId, "{}");
            }
            else turn = script.TryDequeue(out var next) ? next : null;
            if (turn is null)
                throw new FgoPet.Infrastructure.Providers.ProviderRequestException(
                    FgoPet.Infrastructure.Providers.ProviderFailureCategory.ServiceUnavailable, "synthetic provider failure");
            if (turn.Gate is not null) await turn.Gate.Task.ConfigureAwait(false);
            if (turn.Failure is not null) throw turn.Failure;
            if (request.IsAgentRequest && !RawFinalResponses
                && turn.Chunks.LastOrDefault() is { IsComplete: true, FinishReason: "stop" } final)
                turn = turn with { Chunks = [new ChatStreamChunk(
                    OutputEnvelope(string.Concat(turn.Chunks.Select(chunk => chunk.TextDelta)), request),
                    final.IsComplete, final.FinishReason, final.ToolCallDelta, final.ReasoningDelta, final.Usage)] };
            foreach (var chunk in turn.Chunks)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await Task.Yield();
                yield return chunk;
            }
        }
    }

    private sealed record ProviderTurn(IReadOnlyList<ChatStreamChunk> Chunks,
        FgoPet.Infrastructure.Providers.ProviderRequestException? Failure = null,
        TaskCompletionSource<bool>? Gate = null)
    {
        public static ProviderTurn Call(string toolName, string callId, string arguments) => new(
            [new ChatStreamChunk(string.Empty, ToolCallDelta: new(0, id: callId,
                name: ModelToolNameMap.GetWireName(toolName), argumentsDelta: arguments)),
             new ChatStreamChunk(string.Empty, IsComplete: true, FinishReason: "tool_calls")]);

        public static ProviderTurn Final(string text) => new([new ChatStreamChunk(text, IsComplete: true, FinishReason: "stop")]);
        public static ProviderTurn Fail(FgoPet.Infrastructure.Providers.ProviderRequestException error) => new([], error);
        public static ProviderTurn BlockedFinal(string text, TaskCompletionSource<bool> gate) => new(
            [new ChatStreamChunk(text, IsComplete: true, FinishReason: "stop")], Gate: gate);
    }

    private sealed class RecordingSummarizer : IConversationSummarizer
    {
        private const string Summary = """
            任务目标：继续旧任务
            用户已确认的决定：保持原目标
            否定、更正及被替代事项：未确定
            尚未解决的问题：继续当前任务
            下一步：处理剩余事项
            来源消息：assistant-old-1
            """;
        public int SharedBudgetReservations { get; private set; }

        public Task<SummaryAttempt> SummarizeAsync(IChatProvider provider, ChatRequest request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Assert.Equal("context_summary", request.Metadata["fgo_auxiliary"]);
            return Task.FromResult(new SummaryAttempt(Summary, "stop", null));
        }

        public async Task<SummaryAttempt> SummarizeAsync(IChatProvider provider, ChatRequest request,
            IModelRequestBudget budget, CancellationToken cancellationToken)
        {
            SharedBudgetReservations++;
            await budget.ReserveAsync(cancellationToken);
            return await SummarizeAsync(provider, request, cancellationToken);
        }
    }

    private sealed class NativeSummaryRecorder(bool fails = false) : IConversationSummarizer
    {
        public List<ChatRequest> Requests { get; } = [];
        public Task<SummaryAttempt> SummarizeAsync(IChatProvider provider, ChatRequest request, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            Requests.Add(request);
            if (fails) return Task.FromResult(new SummaryAttempt("invalid", "length", null));
            var source = System.Text.RegularExpressions.Regex.Matches(request.Messages[0].Text, @"native-\d+-part-\d+").Last().Value;
            return Task.FromResult(new SummaryAttempt("任务目标：分段读取\n用户已确认的决定：未确定\n否定、更正及被替代事项：未确定\n尚未解决的问题：继续读取\n下一步：依据最新观察回答\n来源消息：" + source, "stop", null));
        }
    }

    private sealed class RecordingTokenMeter : IRequestTokenMeter
    {
        private readonly RequestTokenMeter inner = new();
        public int Maximum { get; private set; }
        public TokenMeasurement Measure(ModelRouteKey route, ChatRequest request)
        {
            var measured = inner.Measure(route, request);
            Maximum = Math.Max(Maximum, measured.InputTokens);
            return measured;
        }
        public void RecordUsage(ModelRouteKey route, ChatRequest request, ChatUsage usage) => inner.RecordUsage(route, request, usage);
        public void Invalidate(ModelRouteKey route) => inner.Invalidate(route);
    }

    private sealed class CharacterTokenMeter : IRequestTokenMeter
    {
        public TokenMeasurement Measure(ModelRouteKey route, ChatRequest request)
        {
            var text = request.IsAgentRequest ? string.Concat(request.ModelMessages.Select(message => message.Content))
                : string.Concat(request.Messages.Select(message => message.Text));
            return new(text.Length, TokenCountKind.Estimated, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))));
        }
        public void RecordUsage(ModelRouteKey route, ChatRequest request, ChatUsage usage) { }
        public void Invalidate(ModelRouteKey route) { }
    }

    private sealed class CompactionTriggeringMeter : IRequestTokenMeter
    {
        public TokenMeasurement Measure(ModelRouteKey route, ChatRequest request)
        {
            var characters = request.IsAgentRequest
                ? request.ModelMessages.Sum(message => message.Content.Length)
                : request.Messages.Sum(message => message.Text.Length);
            var tokens = characters > 7000 ? 30_000 : 3_000;
            return new(tokens, TokenCountKind.Estimated,
                Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{route}:{characters}:{tokens}"))));
        }

        public void RecordUsage(ModelRouteKey route, ChatRequest request, ChatUsage usage) { }
        public void Invalidate(ModelRouteKey route) { }
    }

    private sealed class SyntheticProtector : IProtectedStateProtector, IDisposable
    {
        private readonly byte[] key = RandomNumberGenerator.GetBytes(32);
        public byte[] Protect(ReadOnlySpan<byte> plaintext, string purpose)
        {
            var nonce = RandomNumberGenerator.GetBytes(12);
            var tag = new byte[16];
            var ciphertext = new byte[plaintext.Length];
            using var aes = new AesGcm(key, tag.Length);
            aes.Encrypt(nonce, plaintext, ciphertext, tag, Encoding.UTF8.GetBytes(purpose));
            return [.. nonce, .. tag, .. ciphertext];
        }

        public byte[] Unprotect(ReadOnlySpan<byte> protectedState, string purpose)
        {
            if (protectedState.Length < 28) throw new StateProtectionException("SYNTHETIC_CIPHER_INVALID");
            try
            {
                var plaintext = new byte[protectedState.Length - 28];
                using var aes = new AesGcm(key, 16);
                aes.Decrypt(protectedState[..12], protectedState[28..], protectedState[12..28], plaintext,
                    Encoding.UTF8.GetBytes(purpose));
                return plaintext;
            }
            catch (CryptographicException) { throw new StateProtectionException("SYNTHETIC_CIPHER_INVALID"); }
        }

        public void Dispose() => CryptographicOperations.ZeroMemory(key);
    }
}
