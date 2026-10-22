using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using FgoPet.App.Bootstrap;
using FgoPet.App.Dialogue;
using FgoPet.Core.Dialogue;
using FgoPet.Core.Settings;
using FgoPet.Dialogue.Settings;
using FgoPet.Extensibility;
using FgoPet.Infrastructure.Providers;
using FgoPet.Kernel.Agent;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace FgoPet.App.Tests.Dialogue;

public sealed partial class NativeConversationIntegrationTests
{
    [Fact]
    public async Task Empty_session_full_registered_tools_records_fallback_and_exact_rejection_without_dispatch()
    {
        // Catalog construction only: never start real plugins, credentials, transports or tools.
        await using var composition = new ServiceCollection().AddFgoPet([]).BuildServiceProvider();
        var descriptors = composition.GetRequiredService<PluginCatalog>().Tools.Select(tool => tool.Descriptor).ToArray();
        var tools = descriptors.Select(descriptor => (IToolProvider)new DescriptorOnlyTool(descriptor)).ToArray();
        var settings = new TestSettingsStore(DialogueSettings.Defaults with {
            ModelConnection = new ModelConnectionSettings("custom-openai-compatible", "http://127.0.0.1:8326/v1",
                "buddy/deepseek-v4.1-flash", maxOutputTokens: 2048)
        });
        // Installed Buddy listModels emits id/name/modalities, without context_window.
        // A synthetic provider supplies that absence; no local gateway or upstream is contacted.
        var provider = new CapacityCatalogProvider();
        var records = new List<ContextBudgetDiagnostic>();
        var meter = new EmptySessionMeter();
        await using var host = await TestHost.CreateAsync(provider, null, new RecordingObserver(), settings,
            tokenMeter: meter, extraTools: tools, questions: true, skills: true,
            contextDiagnostics: records.Add);
        SeedOldSession(host);
        host.Orchestrator.StartNewConversation(RoleId);
        var result = await host.Orchestrator.SendAsync(RoleId, "你好", default);
        Assert.Equal(ConversationSendStatus.Failed, result.Status);
        Assert.NotEqual("old-capacity-session", result.ConversationId);
        Assert.Empty(provider.Requests);
        Assert.Single(host.Conversations.LoadMessages(result.ConversationId, RoleId));
        Assert.NotEmpty(meter.Requests);
        Assert.All(meter.Requests, request => Assert.DoesNotContain("OLD_SESSION_RAW_CANARY",
            ChatRequestInputEnvelope.Write("fixture", request), StringComparison.Ordinal));
        var capacity = Assert.Single(records.Where(record => record.Stage == ContextDiagnosticStage.CapacityResolved));
        Assert.Equal(ContextLimitSource.ConservativeFallback, capacity.CapacitySource);
        Assert.Equal(8192, capacity.WindowTokens);
        Assert.Equal(5888, capacity.InputBudget);
        Assert.Equal(1, provider.MetadataReads);
        var rejected = Assert.Single(records.Where(record => record.Stage == ContextDiagnosticStage.Rejected));
        Assert.True(rejected.MeasuredInput > rejected.InputBudget);
        Assert.Equal(descriptors.Length + 3, rejected.ToolCount);
        Assert.True(rejected.ToolDefinitionsInput > rejected.InputBudget);
        Assert.Equal(ContextRejectionPoint.FinalStepInputBudget, rejected.Point);
        Assert.Equal("RUN_CONTEXT_TOO_LARGE", (await host.Runs.LoadAsync(result.RunId!, default))!.Snapshot.ErrorCode);
        SaveCapacityEvidence(records, descriptors.Length, meter.Requests.MaxBy(request => request.Tools?.Count ?? 0)!.Tools!);
    }

    [Fact]
    public async Task Forwarded_capacity_allows_same_empty_session_tools_without_override_or_history_changes()
    {
        await using var composition = new ServiceCollection().AddFgoPet([]).BuildServiceProvider();
        var tools = composition.GetRequiredService<PluginCatalog>().Tools
            .Select(tool => (IToolProvider)new DescriptorOnlyTool(tool.Descriptor)).ToArray();
        // Deliberately synthetic capacity, not a claim about Buddy's actual window.
        var provider = new CapacityCatalogProvider(65536);
        var settings = new TestSettingsStore(DialogueSettings.Defaults with {
            ModelConnection = new ModelConnectionSettings("custom-openai-compatible", "http://127.0.0.1:8326/v1",
                "buddy/deepseek-v4.1-flash", maxOutputTokens: 2048)
        });
        var records = new List<ContextBudgetDiagnostic>();
        await using var host = await TestHost.CreateAsync(provider, null, new RecordingObserver(), settings,
            extraTools: tools, questions: true, skills: true, contextDiagnostics: records.Add);
        SeedOldSession(host);
        host.Orchestrator.StartNewConversation(RoleId);
        var result = await host.Orchestrator.SendAsync(RoleId, "你好", default);
        Assert.Equal(ConversationSendStatus.Completed, result.Status);
        var capacity = Assert.Single(records.Where(record => record.Stage == ContextDiagnosticStage.CapacityResolved));
        Assert.Equal(ContextLimitSource.ProviderMetadata, capacity.CapacitySource);
        Assert.Equal(65536, capacity.WindowTokens);
        Assert.Null(settings.Load().ModelConnection!.ContextWindowOverride);
        Assert.DoesNotContain(records, record => record.Stage == ContextDiagnosticStage.Rejected);
        Assert.Equal(tools.Length + 3, provider.Requests[0].Tools!.Count);
        Assert.All(provider.Requests, request => Assert.DoesNotContain("OLD_SESSION_RAW_CANARY",
            ChatRequestInputEnvelope.Write("fixture", request), StringComparison.Ordinal));
        Assert.Single(host.Conversations.LoadMessages("old-capacity-session", RoleId));
    }

    [Fact]
    public async Task New_session_after_old_history_sends_only_its_own_first_user_message()
    {
        var provider = new ScriptedProvider(ProviderTurn.Final("你好。"));
        await using var host = await TestHost.CreateAsync(provider, null, new RecordingObserver());
        SeedOldSession(host);
        host.Orchestrator.StartNewConversation(RoleId);
        var result = await host.Orchestrator.SendAsync(RoleId, "你好", default);
        Assert.Equal(ConversationSendStatus.Completed, result.Status);
        Assert.NotEqual("old-capacity-session", result.ConversationId);
        Assert.NotEmpty(provider.Requests);
        Assert.All(provider.Requests, request => {
            Assert.DoesNotContain("OLD_SESSION_RAW_CANARY", ChatRequestInputEnvelope.Write("fixture", request));
            Assert.Single(request.ModelMessages.Where(message => message.Role == ModelMessageRole.User));
        });
    }

    private static void SeedOldSession(TestHost host)
    {
        var now = DateTimeOffset.UtcNow;
        var context = new ContentContextKey(RoleId, "test-package", "1.0.0", "default", "persona-v1", "knowledge-v1");
        host.Conversations.CreateConversation("old-capacity-session", RoleId, context, now);
        host.Conversations.Append(new("old-capacity-message", "old-capacity-session", RoleId,
            ChatMessageRole.User, "OLD_SESSION_RAW_CANARY", ChatMessageStatus.Completed, now, context, 1));
        host.Conversations.WriteState("LastActiveConversationId:" + RoleId, "old-capacity-session", now);
    }

    private sealed class DescriptorOnlyTool(ToolDescriptor descriptor) : IToolProvider
    {
        public ToolDescriptor Descriptor => descriptor;
        public ValueTask<ToolResult> InvokeAsync(ToolInvocation invocation, CancellationToken cancellationToken)
            => throw new InvalidOperationException("OFFLINE_TOOL_EXECUTION_FORBIDDEN");
    }

    private sealed class CapacityCatalogProvider(int? window = null) : IChatProvider
    {
        private readonly ScriptedProvider script = new(ProviderTurn.Final("你好。"));
        public string ProviderId => "custom-openai-compatible";
        public string ModelId => "buddy/deepseek-v4.1-flash";
        public int MetadataReads { get; private set; }
        public List<ChatRequest> Requests { get; } = [];
        public Task<IReadOnlyList<ProviderModel>> ListModelsAsync(CancellationToken cancellationToken)
        {
            MetadataReads++;
            return Task.FromResult<IReadOnlyList<ProviderModel>>([new(ModelId, contextWindowTokens: window)]);
        }
        public IAsyncEnumerable<ChatStreamChunk> StreamAsync(ChatRequest request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            if (window is not null) return script.StreamAsync(request, cancellationToken);
            throw new InvalidOperationException("OFFLINE_MODEL_DISPATCH_FORBIDDEN");
        }
    }

    private sealed class EmptySessionMeter : IRequestTokenMeter
    {
        private readonly RequestTokenMeter inner = new();
        public List<ChatRequest> Requests { get; } = [];
        public TokenMeasurement Measure(ModelRouteKey route, ChatRequest request)
        {
            Requests.Add(request);
            return inner.Measure(route, request);
        }
        public void RecordUsage(ModelRouteKey route, ChatRequest request, ChatUsage usage) => inner.RecordUsage(route, request, usage);
        public void Invalidate(ModelRouteKey route) => inner.Invalidate(route);
    }

    private static void SaveCapacityEvidence(List<ContextBudgetDiagnostic> records, int registeredTools,
        IReadOnlyList<ChatToolDefinition> offeredTools)
    {
        var directory = Environment.GetEnvironmentVariable("FGO_PET_CAPACITY_TEST_EVIDENCE");
        if (string.IsNullOrWhiteSpace(directory)) return;
        Directory.CreateDirectory(directory);
        var route = new ModelRouteKey("fixture", "offline", "buddy/deepseek-v4.1-flash", "default");
        var counter = new RequestTokenMeter();
        var rows = offeredTools.Select(tool => new {
            wireName = tool.Name,
            standaloneEnvelopeEstimate = counter.Measure(route, ChatRequest.CreateAgent(RoleId, "fixture",
                [new(ModelMessageRole.User, "你好", [])], tools: [tool])).InputTokens
        });
        File.WriteAllText(Path.Combine(directory, "empty-session.json"), JsonSerializer.Serialize(new {
            realModelCalls = 0, realMetadataCalls = 0, oldSessionRawAbsent = true,
            metadataFixture = "installed Buddy listModels omits capacity", historyCount = 0,
            persona = "synthetic empty persona", runtimeContextProviders = 0,
            registeredTools, offeredToolCount = offeredTools.Count, records, tools = rows,
            measurementBasis = "independent envelopes; estimates are not additive"
        }, new JsonSerializerOptions { WriteIndented = true, Converters = { new JsonStringEnumConverter() } }));
    }
}
