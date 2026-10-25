using FgoPet.App.Dialogue;
using FgoPet.Core.Dialogue;
using FgoPet.Core.Settings;
using FgoPet.Dialogue.Settings;
using FgoPet.Extensibility;
using FgoPet.Infrastructure.Providers;
using Microsoft.Extensions.Logging;
using Xunit;

namespace FgoPet.App.Tests.Dialogue;

public sealed partial class NativeConversationIntegrationTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task Rejected_read_continuation_reports_its_actual_measurement_without_content_or_a_second_dispatch(bool malformedFingerprint, bool throwingSink)
    {
        const string user = "SYNTHETIC_PRIVATE_USER_INPUT";
        const string observation = "SYNTHETIC_PRIVATE_OBSERVATION";
        var provider = new ScriptedProvider(ProviderTurn.Call("fixture.read", "read-1", "{}"), ProviderTurn.Final("Must not dispatch."));
        var tool = new RecordingTool(ToolEffect.ReadOnly, observation + new string('x', 20000));
        var meter = new RejectionMeterProbe(malformedFingerprint);
        var logger = new BudgetRejectionLogger(throwingSink);
        var settings = new TestSettingsStore(DialogueSettings.Defaults with {
            ModelConnection = new ModelConnectionSettings(provider.ProviderId, "https://fixture.invalid/v1", provider.ModelId,
                toolsSupported: true, contextWindowOverride: 65536, maxOutputTokens: 2048)
        });
        await using var host = await TestHost.CreateAsync(new LimitedProvider(provider, 16000), tool, new RecordingObserver(),
            settings: settings, tokenMeter: meter, logger: logger, useNative: false);
        var result = await host.Orchestrator.SendAsync(RoleId, user, default, new(ProjectId));
        Assert.Equal(ConversationSendStatus.Failed, result.Status);
        Assert.Equal("当前内容超出模型可用上下文，且无法安全压缩。原记录已保留，请缩短输入或调整模型容量后重试。", result.SafeError);
        Assert.Single(provider.Requests);
        Assert.Single(tool.Invocations);
        var rejected = Assert.Single(logger.Entries.Where(e => e.Id == 4103));
        Assert.Equal(LogLevel.Warning, rejected.Level);
        Assert.Equal("ordinary_read_continuation", rejected.Fields["Phase"]);
        Assert.Equal("DIALOGUE_INPUT_BUDGET_EXCEEDED", rejected.Fields["ErrorCode"]);
        Assert.Equal(16000, rejected.Fields["InputTokenLimit"]);
        Assert.Equal("estimated", rejected.Fields["CountKind"]);
        Assert.Equal(2, rejected.Fields["NextPrimaryAttempt"]);
        Assert.NotNull(meter.RejectedRequest);
        Assert.NotNull(meter.RejectedMeasurement);
        Assert.Equal(meter.RejectedMeasurement.InputTokens, rejected.Fields["MeasuredInputTokens"]);
        Assert.True(meter.RejectedMeasurement.InputTokens > 16000);
        Assert.Equal(meter.RejectedRequest.EffectiveMessageCount, rejected.Fields["MessageCount"]);
        Assert.Equal(meter.RejectedRequest.Tools!.Count, rejected.Fields["ToolCount"]);
        Assert.Equal(malformedFingerprint ? "unavailable" : meter.RejectedMeasurement.Fingerprint, rejected.Fields["RequestHash"]);
        var text = string.Join("\n", logger.Entries.Select(e => e.Text));
        Assert.DoesNotContain(user, text);
        Assert.DoesNotContain(observation, text);
        Assert.DoesNotContain(RejectionMeterProbe.PrivateFingerprint, text);
    }

    private sealed class RejectionMeterProbe(bool malformedFingerprint) : IRequestTokenMeter
    {
        internal const string PrivateFingerprint = "SYNTHETIC_PRIVATE_INVALID_FINGERPRINT";
        private readonly RequestTokenMeter inner = new();
        internal ChatRequest? RejectedRequest { get; private set; }
        internal TokenMeasurement? RejectedMeasurement { get; private set; }
        public TokenMeasurement Measure(ModelRouteKey route, ChatRequest request)
        {
            var result = inner.Measure(route, request);
            if (request.IsAgentRequest && result.InputTokens > 16000)
            {
                result = malformedFingerprint ? result with { Fingerprint = PrivateFingerprint } : result;
                RejectedRequest = request; RejectedMeasurement = result;
            }
            return result;
        }
        public void RecordUsage(ModelRouteKey route, ChatRequest request, ChatUsage usage) => inner.RecordUsage(route, request, usage);
        public void Invalidate(ModelRouteKey route) => inner.Invalidate(route);
    }

    private sealed class BudgetRejectionLogger(bool throwingSink) : ILogger<ConversationOrchestrator>
    {
        internal sealed record Entry(int Id, LogLevel Level, string Text, Dictionary<string, object?> Fields);
        internal List<Entry> Entries { get; } = [];
        public bool IsEnabled(LogLevel level) => true;
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public void Log<TState>(LogLevel level, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            var fields = state is IEnumerable<KeyValuePair<string, object?>> pairs ? pairs.ToDictionary(p => p.Key, p => p.Value) : [];
            Entries.Add(new(eventId.Id, level, formatter(state, exception), fields));
            if (throwingSink && eventId.Id == 4103) throw new InvalidOperationException("SYNTHETIC_PRIVATE_SINK_ERROR");
        }
    }
}
