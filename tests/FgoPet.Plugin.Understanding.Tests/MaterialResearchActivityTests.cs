using FgoPet.App.Dialogue;
using FgoPet.Core.Dialogue;
using FgoPet.Core.Settings;
using FgoPet.Extensibility;
using FgoPet.Infrastructure.Persistence;
using FgoPet.Kernel.Agent;
using FgoPet.Understanding;
using FgoPet.Understanding.Contracts;
using Xunit;

namespace FgoPet.Plugin.Understanding.Tests;

public sealed class MaterialResearchActivityTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), "fgopet-material-activity-" + Guid.NewGuid().ToString("N"), "runtime.db");
    private readonly Host _host = new();
    private readonly Reader _reader = new();
    private readonly Model _model = new();
    private readonly Source _source = new();
    private readonly DialogueContextLifetime _lifetime = new();
    private readonly TopicMaterialStore _store;
    private readonly MaterialResearchActivity _activity;
    public MaterialResearchActivityTests()
    {
        var db = new RuntimeDatabase(_path, pooling: false);
        new RuntimeDatabaseMigrator(db).Migrate();
        _store = new(db);
        _activity = new(_host, _reader, _store, _model, _source, _lifetime, TimeProvider.System);
    }
    [Fact] public async Task One_topic_one_search_source_bound_material_and_no_understanding_mutation()
    {
        await _activity.RunOnceAsync();
        var result = Assert.Single(_store.GetActive(new("mash", "p")));
        Assert.Equal("interest", result.TopicKey);
        Assert.Equal("https://example.org/article", result.SourceUrl);
        Assert.Equal("Selective forgetting limits conflicting memories.", result.Summary);
        Assert.Equal(1, _source.Calls);
        Assert.Equal(2, _model.Dispatches);
        Assert.Equal(1, _reader.Entry.Revision);
        await _activity.RunOnceAsync();
        Assert.Equal(1, _source.Calls); // Durable cooldown survives a repeated pulse.
    }
    [Fact] public async Task Disabled_host_and_pending_interests_never_dispatch()
    {
        _host.Allowed = false;
        await _activity.RunOnceAsync(); Assert.Equal(0, _model.Dispatches);
        _host.Allowed = true; _reader.Entry = _reader.Entry with { Effect = UnderstandingEffect.Pending };
        await _activity.RunOnceAsync(); Assert.Equal(0, _model.Dispatches);
    }
    [Fact] public async Task Foreground_has_priority_and_cancels_background_before_search()
    {
        using (var foreground = _lifetime.Acquire(default)) await _activity.RunOnceAsync();
        Assert.Equal(0, _model.Dispatches);
        _model.AfterQuery = () => { using var foreground = _lifetime.Acquire(default); };
        await _activity.RunOnceAsync();
        Assert.Equal(0, _source.Calls); Assert.Empty(_store.GetActive(new("mash", "p")));
    }
    [Fact] public async Task Revocation_during_search_discards_result_without_second_model_call()
    {
        _source.AfterSearch = () => _host.Allowed = false;
        await _activity.RunOnceAsync();
        Assert.Equal(1, _model.Dispatches); Assert.Empty(_store.GetActive(new("mash", "p")));
    }
    [Fact] public async Task Changed_topic_revision_during_synthesis_cannot_publish()
    {
        _model.AfterSummary = () => _reader.Entry = _reader.Entry with { Revision = 2 };
        await _activity.RunOnceAsync(); Assert.Empty(_store.GetActive(new("mash", "p")));
    }
    [Fact] public async Task Missing_dispatch_and_invalid_source_index_cannot_publish()
    {
        _model.SkipDispatch = true;
        await _activity.RunOnceAsync(); Assert.Equal(0, _source.Calls);
        _model.SkipDispatch = false; _model.Draft = new(9, "fabricated", "bad source");
        await _activity.RunOnceAsync(); Assert.Empty(_store.GetActive(new("mash", "p")));
    }
    [Fact] public async Task Failed_search_is_not_retried_or_refunded()
    {
        _source.Fail = true;
        await _activity.RunOnceAsync(); await _activity.RunOnceAsync();
        Assert.Equal(1, _source.Calls); Assert.Empty(_store.GetActive(new("mash", "p")));
    }
    [Theory]
    [InlineData(768, true)]
    [InlineData(769, false)]
    public async Task Summary_dispatch_enforces_its_own_exact_output_ceiling(int limit, bool allowed)
    {
        _model.SummaryOutputTokens = limit;
        await _activity.RunOnceAsync();
        Assert.Equal(allowed ? 1 : 0, _store.GetActive(new("mash", "p")).Count);
        Assert.Equal(allowed ? 2 : 1, _model.Dispatches);
    }
    [Fact] public async Task Truncated_summary_retains_a_safe_specific_code_without_saving_or_retrying()
    {
        _model.SummaryFailure = new AgentProtocolException("MODEL_OUTPUT_LIMIT");
        await _activity.RunOnceAsync();
        Assert.Equal("MATERIAL_RESEARCH_MODEL_OUTPUT_LIMIT", _activity.LastCode);
        Assert.Empty(_store.GetActive(new("mash", "p")));
        Assert.Equal(2, _model.Dispatches);
    }
    private sealed class Host : IMaterialResearchHost
    {
        public bool Allowed = true;
        private readonly MaterialResearchConfiguration _config = new(new("c", "mash", "p"),
            new ModelConnectionSettings("fixture", "https://example.org/v1", "model", contextWindowOverride: 32768), "grant-1");
        public MaterialResearchConfiguration? Capture() => Allowed ? _config : null;
        public bool TryCommit(MaterialResearchConfiguration config, Action action)
        { if (!Allowed || config != _config) return false; action(); return true; }
    }
    private sealed class Reader : IUnderstandingReader
    {
        public UnderstandingEntry Entry = new("interest", new("mash", "p"), 1, UnderstandingDomain.Self,
            UnderstandingKind.CurrentInterest, "assistant", "agent memory", "tentative", "fixture", [],
            UnderstandingReview.Approved, UnderstandingEffect.Approved, UnderstandingLife.Active,
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddDays(1), 1);
        public UnderstandingProjection Query(ConversationScope scope, string query, bool historical = false, string? entryId = null) => new(1, [Entry]);
    }
    private sealed class Source : IMaterialResearchSource
    {
        public int Calls; public Action? AfterSearch; public bool Fail;
        public Task<IReadOnlyList<MaterialSearchResult>> SearchAsync(ToolScope scope, string query, CancellationToken token, string? activityId = null)
        {
            Calls++; AfterSearch?.Invoke(); if (Fail) throw new HttpRequestException("synthetic failure");
            return Task.FromResult<IReadOnlyList<MaterialSearchResult>>([new("Article", "https://example.org/article", "Selective forgetting limits conflicting memories.")]);
        }
    }
    private sealed class Model : IMaterialResearchModel
    {
        public int Dispatches; public bool SkipDispatch; public Action? AfterQuery; public Action? AfterSummary;
        public int SummaryOutputTokens = 384;
        public Exception? SummaryFailure;
        public MaterialResearchDraft Draft = new(0, "Selective forgetting limits conflicting memories.", "Directly related to agent memory.");
        private void Dispatch(MaterialResearchRequest r, IChatDispatchGate gate, CancellationToken token, int output = 384)
        {
            if (SkipDispatch) return;
            var chat = ChatRequest.CreateAgent(r.Scope.RoleId, r.Scope.ConversationId, [new(ModelMessageRole.System, "synthetic request", [])],
                metadata: new Dictionary<string, string> { ["fgo_auxiliary"] = "topic_material_research", ["fgo_activity_id"] = r.ActivityId }, maxOutputTokens: output);
            gate.Dispatch(chat, ModelRouteKey.From(r.Connection), () => ++Dispatches, token);
        }
        public Task<string?> GenerateQueryAsync(MaterialResearchRequest r, IChatDispatchGate gate, Action<CancellationToken> current, CancellationToken token)
        { Dispatch(r, gate, token); AfterQuery?.Invoke(); return Task.FromResult<string?>("agent memory selective forgetting"); }
        public Task<MaterialResearchDraft?> SummarizeAsync(MaterialResearchRequest r, IReadOnlyList<MaterialSearchResult> results,
            IChatDispatchGate gate, Action<CancellationToken> current, CancellationToken token)
        { Dispatch(r, gate, token, SummaryOutputTokens); AfterSummary?.Invoke();
            if (SummaryFailure is not null) throw SummaryFailure;
            return Task.FromResult<MaterialResearchDraft?>(Draft); }
    }
    public void Dispose()
    {
        var directory = Path.GetDirectoryName(_path)!;
        if (Directory.Exists(directory)) Directory.Delete(directory, true);
    }
}
