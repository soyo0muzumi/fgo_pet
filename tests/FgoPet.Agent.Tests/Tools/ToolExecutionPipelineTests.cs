using System.Text.Json;
using FgoPet.Agent.Tests.TestDoubles;
using FgoPet.Extensibility;
using FgoPet.Kernel.Agent;
using Xunit;

namespace FgoPet.Agent.Tests.Tools;

public sealed class ToolExecutionPipelineTests
{
    [Fact]
    public async Task Registry_exposes_active_catalog_without_owning_lifecycle()
    {
        using var host = new Host();
        Assert.Empty(host.Registry.GetTools(host.Scope));
        await host.StartAsync();
        Assert.Equal("fixture.read", Assert.Single(host.Registry.GetTools(host.Scope)).Name);
        host.Runtime.CloseAdmission();
        Assert.Empty(host.Registry.GetTools(host.Scope));
        Assert.False(host.Registry.TryResolve("fixture.read", host.Scope, out _));
        Assert.Empty(host.Provider.Invocations);
    }

    [Theory]
    [InlineData("unregistered", "TOOL_NOT_FOUND")]
    [InlineData("unresolved", "TOOL_NOT_FOUND")]
    [InlineData("inactive", "TOOL_NOT_FOUND")]
    [InlineData("closed", "TOOL_NOT_FOUND")]
    [InlineData("scope", "TOOL_SCOPE_DENIED")]
    [InlineData("role", "TOOL_SCOPE_DENIED")]
    [InlineData("project", "TOOL_SCOPE_DENIED")]
    [InlineData("schema", "TOOL_INVALID_ARGUMENTS")]
    [InlineData("invalidJson", "TOOL_INVALID_ARGUMENTS")]
    [InlineData("largeArguments", "TOOL_ARGUMENTS_TOO_LARGE")]
    [InlineData("command", "TOOL_AUTHORIZATION_DENIED")]
    public async Task Rejection_never_commits_intent_or_invokes_provider(string rejection, string error)
    {
        using var host = new Host(new(effect: rejection == "command" ? ToolEffect.Command : ToolEffect.ReadOnly,
            schema: "{\"type\":\"object\",\"properties\":{\"text\":{\"type\":\"string\"}},\"required\":[\"text\"],\"additionalProperties\":false}"));
        if (rejection != "inactive") await host.StartAsync();
        if (rejection == "closed") host.Runtime.CloseAdmission();
        var request = host.Request(rejection switch
        {
            "schema" => "{\"text\":1}",
            "invalidJson" => "{",
            "largeArguments" => "{\"text\":\"" + new string('a', 65536) + "\"}",
            _ => "{\"text\":\"fixture\"}"
        });
        if (rejection == "unregistered") request = request with { Call = request.Call with { Name = "missing" } };
        if (rejection == "unresolved") request = request with { Call = request.Call with { IsResolved = false } };
        if (rejection == "scope") request = request with { Identity = request.Identity with { Scope = new("other", "role", null) } };
        if (rejection == "role") request = request with { Identity = request.Identity with { Scope = host.Scope with { RoleId = "other" } } };
        if (rejection == "project") request = request with { Identity = request.Identity with { Scope = host.Scope with { ProjectId = "other" } } };
        var result = await host.Pipeline.AdvanceAsync(request, host.Intent, CancellationToken.None);
        Assert.Equal(ToolExecutionOutcomeKind.Completed, result.Kind);
        Assert.False(result.Result!.Success);
        Assert.Equal(error, result.Result.ErrorCode);
        Assert.Equal(JsonValueKind.Object, result.Result.Payload.ValueKind);
        Assert.Equal(0, host.Intent.Commits);
        Assert.Empty(host.Provider.Invocations);
    }

    [Fact]
    public async Task Provider_receives_host_context_only_after_intent_commit()
    {
        using var host = new Host(new(effect: ToolEffect.Proposal));
        await host.StartAsync();
        host.Provider.Handler = (invocation, _) =>
        {
            Assert.Equal(1, host.Intent.Commits);
            Assert.Equal("run", invocation.ExecutionContext!.RunId);
            Assert.Equal(2, invocation.ExecutionContext.StepNumber);
            Assert.Equal("call", invocation.ExecutionContext.CallId);
            Assert.NotEqual("forged", invocation.ExecutionContext.IdempotencyKey);
            return ValueTask.FromResult(new ToolResult(true, invocation.Arguments));
        };
        var result = await host.Pipeline.AdvanceAsync(host.Request("{\"executionContext\":\"forged\"}"), host.Intent, CancellationToken.None);
        Assert.True(result.Result!.Success);
        Assert.Single(host.Provider.Invocations);
    }

    [Fact]
    public async Task Intent_failure_prevents_invocation_and_propagates_to_state_owner()
    {
        using var host = new Host();
        await host.StartAsync();
        host.Intent.BeforeCommit = () => throw new IOException("synthetic storage failure");
        await Assert.ThrowsAsync<IOException>(() => host.Pipeline.AdvanceAsync(host.Request(), host.Intent, CancellationToken.None).AsTask());
        Assert.Empty(host.Provider.Invocations);
    }

    [Fact]
    public async Task Invocation_waits_for_intent_to_finish_and_stopped_plugins_cannot_run()
    {
        using var host = new Host();
        await host.StartAsync();
        var persisted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        host.Intent.PersistAsync = token => new ValueTask(persisted.Task.WaitAsync(token));
        var executing = host.Pipeline.AdvanceAsync(host.Request(), host.Intent, CancellationToken.None).AsTask();
        Assert.False(executing.IsCompleted);
        Assert.Empty(host.Provider.Invocations);
        persisted.SetResult();
        Assert.True((await executing).Result!.Success);
        Assert.Single(host.Provider.Invocations);
        await host.Runtime.StopAsync();
        Assert.Equal("TOOL_NOT_FOUND", (await host.Pipeline.AdvanceAsync(host.Request(), host.Intent, CancellationToken.None)).Result!.ErrorCode);
        Assert.Single(host.Provider.Invocations);
    }

    [Theory]
    [InlineData("admission")]
    [InlineData("fence")]
    public async Task Guards_are_rechecked_after_async_intent_before_actual_invoke(string guard)
    {
        using var host = new Host();
        await host.StartAsync();
        host.Intent.BeforeCommit = () =>
        {
            if (guard == "admission") host.Runtime.CloseAdmission();
            else host.Fence.IsCurrent = false;
        };
        if (guard == "fence")
            await Assert.ThrowsAsync<AgentStateException>(() => host.Pipeline.AdvanceAsync(host.Request(), host.Intent, CancellationToken.None).AsTask());
        else
            Assert.Equal("TOOL_NOT_FOUND", (await host.Pipeline.AdvanceAsync(host.Request(), host.Intent, CancellationToken.None)).Result!.ErrorCode);
        Assert.Empty(host.Provider.Invocations);
    }

    [Fact]
    public async Task Ordinary_provider_failure_is_a_safe_recoverable_observation()
    {
        using var host = new Host();
        await host.StartAsync();
        host.Provider.Handler = (_, _) => throw new InvalidOperationException("private backend details");
        var result = await host.Pipeline.AdvanceAsync(host.Request(), host.Intent, CancellationToken.None);
        Assert.Equal(ToolExecutionOutcomeKind.Completed, result.Kind);
        Assert.Equal("TOOL_EXECUTION_FAILED", result.Result!.ErrorCode);
        Assert.DoesNotContain("private", JsonSerializer.Serialize(result));
        Assert.Single(host.Provider.Invocations);
    }

    [Fact]
    public async Task Cancellation_propagates_and_never_becomes_an_error_observation()
    {
        using var host = new Host();
        await host.StartAsync();
        using var source = new CancellationTokenSource();
        source.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => host.Pipeline.AdvanceAsync(host.Request(), host.Intent, source.Token).AsTask());
        Assert.Empty(host.Provider.Invocations);
        host.Provider.Handler = (_, _) => throw new OperationCanceledException();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => host.Pipeline.AdvanceAsync(host.Request(), host.Intent, CancellationToken.None).AsTask());
        Assert.Single(host.Provider.Invocations);
    }

    [Fact]
    public async Task Provider_payload_is_cloned_and_default_payload_is_valid_json()
    {
        using var host = new Host();
        await host.StartAsync();
        using var payload = JsonDocument.Parse("{\"count\":3}");
        host.Provider.Handler = (_, _) => ValueTask.FromResult(new ToolResult(true, payload.RootElement));
        var result = await host.Pipeline.AdvanceAsync(host.Request(), host.Intent, CancellationToken.None);
        payload.Dispose();
        Assert.Equal(3, result.Result!.Payload.GetProperty("count").GetInt32());
        host.Provider.Handler = (_, _) => ValueTask.FromResult(new ToolResult(false, default, "FIXTURE_FAILURE"));
        result = await host.Pipeline.AdvanceAsync(host.Request(), host.Intent, CancellationToken.None);
        Assert.Equal("{}", result.Result!.Payload.GetRawText());
    }

    [Theory]
    [InlineData("oversize", "TOOL_OUTPUT_TOO_LARGE")]
    [InlineData("disposed", "TOOL_INVALID_OUTPUT")]
    [InlineData("unsafeError", "TOOL_EXECUTION_FAILED")]
    public async Task Invalid_provider_output_is_bounded_and_safe(string mode, string code)
    {
        using var host = new Host();
        await host.StartAsync();
        var document = JsonDocument.Parse("{}");
        var disposed = document.RootElement;
        document.Dispose();
        host.Provider.Handler = (_, _) => ValueTask.FromResult(mode switch
        {
            "oversize" => new ToolResult(true, JsonSerializer.SerializeToElement(new string('x', 65536))),
            "disposed" => new ToolResult(true, disposed),
            _ => new ToolResult(false, default, "private backend details " + new string('x', 200))
        });
        var result = await host.Pipeline.AdvanceAsync(host.Request(), host.Intent, CancellationToken.None);
        Assert.Equal(code, result.Result!.ErrorCode);
        Assert.False(result.Result.Success);
        Assert.Equal("{}", result.Result.Payload.GetRawText());
    }

    private sealed class Host : IDisposable
    {
        public ToolScope Scope { get; } = new("conversation", "role", "project");
        public RecordingToolProvider Provider { get; }
        public PluginRuntime Runtime { get; }
        public ToolRegistry Registry { get; }
        public ToolExecutionPipeline Pipeline { get; }
        public Intent Intent { get; } = new();
        public Fence Fence { get; } = new();
        public Host(RecordingToolProvider? provider = null)
        {
            Provider = provider ?? new();
            var catalog = PluginCatalog.Create([new Plugin(Provider)]);
            Runtime = new(catalog);
            Registry = new(catalog, Runtime);
            Pipeline = new(Registry, new ToolExecutor(Registry, Scope, Fence));
        }
        public async Task StartAsync() => Assert.True((await Runtime.StartAsync(CancellationToken.None)).Succeeded);
        public ToolExecutionRequest Request(string arguments = "{}")
            => new(new("run", "root", Scope, "model", 1), 2, new("call", Provider.Descriptor.Name, arguments));
        public void Dispose() => Runtime.Dispose();
    }
    private sealed class Plugin(IToolProvider provider) : IFgoPetPlugin
    {
        public PluginManifest Manifest { get; } = new("fixture.tools", "1.0.0", 1, []);
        public PluginContributions Contributions { get; } = new([provider], [], []);
        public ValueTask StartAsync(CancellationToken token) => ValueTask.CompletedTask;
        public ValueTask StopAsync(CancellationToken token) => ValueTask.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
    private sealed class Intent : IToolExecutionIntent
    {
        public int Commits { get; private set; }
        public Action? BeforeCommit { get; set; }
        public Func<CancellationToken, ValueTask>? PersistAsync { get; set; }
        public async ValueTask CommitStartedAsync(ToolDescriptor descriptor, CancellationToken token)
        {
            BeforeCommit?.Invoke();
            if (PersistAsync is not null) await PersistAsync(token);
            Commits++;
        }
    }
    private sealed class Fence : IAgentRunFence
    {
        public bool IsCurrent { get; set; } = true;
        public void EnsureCurrent(AgentRunIdentity identity, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            if (!IsCurrent) throw new AgentStateException("RUN_FENCED");
        }
    }
}
