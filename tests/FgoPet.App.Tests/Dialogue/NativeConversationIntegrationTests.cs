using System.Collections.Concurrent;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using FgoPet.App.Dialogue;
using FgoPet.Core.Dialogue;
using FgoPet.Core.Packs;
using FgoPet.Core.Settings;
using FgoPet.Dialogue.Settings;
using FgoPet.Extensibility;
using FgoPet.Infrastructure.Dialogue;
using FgoPet.Infrastructure.Packs;
using FgoPet.Infrastructure.Persistence;
using FgoPet.Infrastructure.Providers;
using FgoPet.Kernel.Agent;
using FgoPet.Kernel.Conversation;
using FgoPet.Platform.Secrets;
using Microsoft.Extensions.Logging;
using Xunit;

namespace FgoPet.App.Tests.Dialogue;

public sealed class NativeConversationIntegrationTests
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
        Assert.Equal(3, checkpoint.CompletedSteps.Sum(step => step.Calls.Length));
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
        Assert.DoesNotContain(provider.Requests[^1].ModelMessages, message => message.Role == ModelMessageRole.Tool);
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
        var secondInput = provider.Requests[2].ModelMessages;
        ModelProtocol.ValidateTranscript(secondInput);
        Assert.Contains(secondInput, m => m.Role == ModelMessageRole.Tool && m.Content.Contains("tool observation", StringComparison.Ordinal));
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
        Assert.Equal(2, provider.Requests.Count);
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
                && message.Content.Contains("tool observation", StringComparison.Ordinal)));
        var checkpoint = await host.Runs.LoadAsync(result.RunId!, default);
        Assert.Equal(AgentRunStatus.Completed, checkpoint!.Snapshot.Status);
        Assert.Equal(2, checkpoint.Snapshot.ModelRequests);
        Assert.Equal(1, checkpoint.Snapshot.ToolCalls);
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
        Assert.Collection(provider.Requests, _ => { }, _ => { });
        Assert.Equal(3, checkpoint!.Snapshot.ModelRequests);
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

    private sealed class TestHost : IAsyncDisposable
    {
        private const string ActiveConversationKey = "LastActiveConversationId:" + RoleId;
        private readonly string directory = Path.Combine(Path.GetTempPath(), "fgo-native-conversation-" + Guid.NewGuid().ToString("N"));
        private readonly SyntheticProtector protector = new();
        private readonly PluginRuntime plugins;
        private readonly NativeConversationRuntime nativeRuntime;
        private readonly DateTimeOffset now = new(2026, 10, 6, 0, 0, 0, TimeSpan.Zero);

        private TestHost(ScriptedProvider provider, IToolProvider tool, RecordingObserver observer,
            TestSettingsStore? settings, bool enableSummary, IConversationSummarizer? summarizer,
            IRequestTokenMeter? tokenMeter, IAgentContextProvider? context, ILogger<ConversationOrchestrator>? logger)
        {
            Directory.CreateDirectory(directory);
            Database = new(Path.Combine(directory, "runtime.db"), pooling: false);
            new RuntimeDatabaseMigrator(Database).Migrate();
            Conversations = new(Database);
            Runs = new(Database, protector);
            Deliveries = new(Database, protector, Runs);
            Settings = settings ?? TestSettingsStore.Create();

            var plugin = new TestPlugin(tool, observer, context);
            var catalog = PluginCatalog.Create([plugin]);
            plugins = new(catalog);
            nativeRuntime = new(Runs, new ToolRegistry(catalog, plugins), new NativeAgentExtensions(Context: new(catalog, plugins)));
            var activation = plugins.StartAsync(default).GetAwaiter().GetResult();
            if (!activation.Succeeded) throw new InvalidOperationException("TEST_PLUGIN_ACTIVATION_FAILED");
            var capabilities = new ConversationCapabilityRouter(catalog, plugins);
            ContextStore = enableSummary ? new SqliteConversationContextStore(Database) : null;
            var summaries = ContextStore is null || summarizer is null ? null
                : new ConversationSummaryService(ContextStore, summarizer, tokenMeter ?? new RequestTokenMeter(), TimeProvider.System);
            Orchestrator = new(new TestProviderResolver(provider), new TestContentResolver(), Conversations,
                new PromptComposer(tokenMeter), TimeProvider.System, Settings, summaries, capabilities,
                tokenMeter: tokenMeter, contextStore: ContextStore, nativeRuntime: nativeRuntime,
                agentRuns: Runs, finalDeliveries: Deliveries, logger: logger);
        }

        public RuntimeDatabase Database { get; }
        public SqliteConversationRepository Conversations { get; }
        public SqliteAgentRunStore Runs { get; }
        public SqliteAgentFinalDeliveryStore Deliveries { get; }
        public TestSettingsStore Settings { get; }
        public ConversationOrchestrator Orchestrator { get; }
        public SqliteConversationContextStore? ContextStore { get; }

        public static Task<TestHost> CreateAsync(ScriptedProvider provider, IToolProvider tool,
            RecordingObserver observer, TestSettingsStore? settings = null, bool enableSummary = false,
            IConversationSummarizer? summarizer = null, IRequestTokenMeter? tokenMeter = null, IAgentContextProvider? context = null,
            ILogger<ConversationOrchestrator>? logger = null)
        {
            return Task.FromResult(new TestHost(provider, tool, observer, settings, enableSummary, summarizer, tokenMeter, context, logger));
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

        private sealed class TestProviderResolver(ScriptedProvider provider) : IChatProviderResolver
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

        private sealed class TestPlugin(IToolProvider tool, RecordingObserver observer, IAgentContextProvider? context) : IFgoPetPlugin
        {
            public PluginManifest Manifest { get; } = new("fixture.native-conversation", "1.0.0", 1, []);
            public PluginContributions Contributions { get; } = PluginContributions.Empty with
            {
                Tools = [tool],
                PostTurnObservers = [observer],
                AgentContexts = context is null ? [] : [context],
            };
            public ValueTask StartAsync(CancellationToken stoppingToken) => ValueTask.CompletedTask;
            public ValueTask StopAsync(CancellationToken cancellationToken) => ValueTask.CompletedTask;
            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
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
        private readonly TaskCompletionSource<bool> firstRequestReceived = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public string ProviderId => "fixture";
        public string ModelId => "fixture-model";
        public IReadOnlyList<ChatRequest> Requests => requests.ToArray();
        public TaskCompletionSource<bool> FirstRequestReceived => firstRequestReceived;
        public Task<IReadOnlyList<ProviderModel>> ListModelsAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<ProviderModel>>([new ProviderModel(ModelId)]);

        public async IAsyncEnumerable<ChatStreamChunk> StreamAsync(ChatRequest request,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
        {
            requests.Enqueue(request);
            firstRequestReceived.TrySetResult(true);
            if (!script.TryDequeue(out var turn))
                throw new FgoPet.Infrastructure.Providers.ProviderRequestException(
                    FgoPet.Infrastructure.Providers.ProviderFailureCategory.ServiceUnavailable, "synthetic provider failure");
            if (turn.Gate is not null) await turn.Gate.Task.ConfigureAwait(false);
            if (turn.Failure is not null) throw turn.Failure;
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
