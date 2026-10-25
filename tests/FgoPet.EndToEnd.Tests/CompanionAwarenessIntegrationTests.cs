using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using System.Text.Json;
using FgoPet.App.Dialogue;
using FgoPet.Core.Dialogue;
using FgoPet.Core.Packs;
using FgoPet.Core.Settings;
using FgoPet.Extensibility;
using FgoPet.Infrastructure.Providers;
using FgoPet.Kernel.Agent;
using FgoPet.Kernel.Companion;
using FgoPet.Plugin.DesktopContext;
using Xunit;
using Xunit.Abstractions;

namespace FgoPet.EndToEnd.Tests;

/// <summary>
/// Public-contract integration of the real activity model, coordinator, dialogue lifetime, desktop context,
/// and both prompt assemblers. The host and transport use synthetic sources; this is not Windows UIA coverage.
/// </summary>
public sealed class CompanionAwarenessIntegrationTests(ITestOutputHelper output)
{
    [Fact]
    public async Task Background_source_change_dispatches_without_a_user_turn_and_enters_both_future_prompts_as_tentative_data()
    {
        await using var fixture = await Fixture.Create(output);
        fixture.Host.PublishFocusCompleted();
        fixture.Coordinator.Start(CancellationToken.None);
        await fixture.Completed.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var background = Assert.Single(fixture.Transport.Requests);
        Assert.Empty(background.Messages);
        Assert.All(background.ModelMessages, message => Assert.Equal(ModelMessageRole.System, message.Role));
        Assert.Null(background.Tools);
        Assert.Equal("companion_activity", background.Metadata["fgo_auxiliary"]);
        Assert.True(Guid.TryParseExact(background.Metadata["fgo_activity_id"], "N", out _));
        Assert.Equal(background.Metadata["fgo_activity_id"], Assert.Single(fixture.Budget.Charges).ActivityId);
        Assert.Equal(1, fixture.Transport.GateCalls);

        var ordinary = fixture.BuildOrdinaryPrompt();
        var ordinaryNote = Assert.Single(ordinary.Messages, message => message.Text.Contains("tentative_interpretation", StringComparison.Ordinal));
        Assert.Equal(ChatMessageRole.System, ordinaryNote.Role);
        Assert.StartsWith("<data source=", ordinaryNote.Text);
        Assert.Contains("tentative_not_user_confirmed", ordinaryNote.Text);
        Assert.Contains("activity.focus", ordinaryNote.Text);
        Assert.Contains("readCoverage", ordinaryNote.Text);
        Assert.Single(ordinary.Messages, message => message.Role == ChatMessageRole.User);

        var native = await fixture.BuildNativePrompt();
        var nativeNote = Assert.Single(native, message => message.Content.Contains("tentative_interpretation", StringComparison.Ordinal));
        Assert.Equal(ModelMessageRole.System, nativeNote.Role);
        Assert.StartsWith("<data source=\"desktop-context:ambient-context:", nativeNote.Content);
        Assert.Contains("tentative_not_user_confirmed", nativeNote.Content);
        Assert.Single(native, message => message.Role == ModelMessageRole.User);
        Assert.Empty(nativeNote.ToolCalls);
        Assert.Single(fixture.Transport.Requests); // Assembly itself cannot dispatch or create another activity.
        Assert.Contains(fixture.Stages, stage => stage == "dispatched");
        Assert.Contains(fixture.Stages, stage => stage == "completed");
    }

    [Theory]
    [InlineData("background_not_approved")]
    [InlineData("source_not_approved")]
    [InlineData("budget_unavailable")]
    public async Task Missing_owner_permission_or_budget_never_dispatches_or_projects_an_interpretation(string denial)
    {
        await using var fixture = await Fixture.Create(output);
        if (denial == "background_not_approved") fixture.Host.BackgroundApproved = false;
        if (denial == "source_not_approved") fixture.Host.SourceApproved = false;
        if (denial == "budget_unavailable") fixture.Budget.Allow = false;
        fixture.Host.PublishFocusCompleted();
        await fixture.Coordinator.PulseAsync();

        Assert.Empty(fixture.Transport.Requests);
        Assert.Empty(fixture.Budget.Charges);
        await fixture.AssertNoInterpretationInEitherPrompt();
        if (denial == "source_not_approved")
            Assert.DoesNotContain(fixture.BuildOrdinaryPrompt().Messages, message => message.Text.Contains("activity.focus", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Source_revocation_at_or_after_handoff_removes_sources_and_rejects_late_model_results(bool afterHandoff)
    {
        await using var fixture = await Fixture.Create(output);
        fixture.Transport.HoldBeforeHandoff = !afterHandoff;
        fixture.Transport.HoldResponse = afterHandoff;
        fixture.Host.PublishFocusCompleted();
        var activity = fixture.Coordinator.PulseAsync();
        await (afterHandoff ? fixture.Transport.Dispatched.Task : fixture.Transport.Prepared.Task).WaitAsync(TimeSpan.FromSeconds(5));
        fixture.Host.RevokeSources();
        fixture.Transport.Release.TrySetResult(); // Simulate a transport that completes even after cancellation.
        await activity.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(afterHandoff ? 1 : 0, fixture.Transport.Requests.Count);
        Assert.Equal(afterHandoff ? 1 : 0, fixture.Budget.Charges.Count);
        await fixture.AssertNoInterpretationInEitherPrompt();
        Assert.DoesNotContain(fixture.BuildOrdinaryPrompt().Messages, message => message.Text.Contains("activity.focus", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_foreground_turn_preempts_background_judgment_and_late_result_cannot_enter_its_context()
    {
        await using var fixture = await Fixture.Create(output);
        fixture.Transport.HoldResponse = true;
        fixture.Host.PublishFocusCompleted();
        var activity = fixture.Coordinator.PulseAsync();
        await fixture.Transport.Dispatched.Task.WaitAsync(TimeSpan.FromSeconds(5));
        using (fixture.Lifetime.Acquire(CancellationToken.None))
        {
            await fixture.Transport.Cancelled.Task.WaitAsync(TimeSpan.FromSeconds(5));
            fixture.Transport.Release.TrySetResult();
            await activity.WaitAsync(TimeSpan.FromSeconds(5));
            await fixture.AssertNoInterpretationInEitherPrompt();
        }
        fixture.Coordinator.NotifyChanged("focus.completed");
        await fixture.Coordinator.PulseAsync();
        Assert.Single(fixture.Transport.Requests);
        Assert.Single(fixture.Budget.Charges); // Dispatched work is neither refunded nor replayed automatically.
    }

    [Fact]
    public async Task A_changed_conversation_cannot_reuse_a_previously_completed_interpretation()
    {
        await using var fixture = await Fixture.Create(output);
        fixture.Host.PublishFocusCompleted();
        await fixture.Coordinator.PulseAsync();
        Assert.Contains(fixture.BuildOrdinaryPrompt().Messages, message => message.Text.Contains("tentative_interpretation", StringComparison.Ordinal));
        var oldScope = fixture.Host.Scope;
        fixture.Host.ChangeScope(new("different-conversation", "mash", "project-fixture"));

        Assert.Empty(fixture.Desktop.BuildPrompt(oldScope, "A real later user message", false));
        await fixture.AssertNoInterpretationInEitherPrompt();
        Assert.Single(fixture.Transport.Requests);
    }

    [Fact]
    public async Task Shutdown_drains_a_pending_transport_and_plugin_stop_removes_its_prompt_contributions()
    {
        await using var fixture = await Fixture.Create(output);
        fixture.Transport.HoldResponse = true;
        fixture.Host.PublishFocusCompleted();
        var activity = fixture.Coordinator.PulseAsync();
        await fixture.Transport.Dispatched.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var stop = fixture.Coordinator.StopAsync();
        await fixture.Transport.Cancelled.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(stop.IsCompleted);
        fixture.Transport.Release.TrySetResult();
        await Task.WhenAll(activity, stop).WaitAsync(TimeSpan.FromSeconds(5));
        await fixture.Runtime.StopAsync(CancellationToken.None);

        fixture.Host.PublishFocusCompleted();
        await fixture.Coordinator.PulseAsync();
        Assert.Single(fixture.Transport.Requests);
        Assert.Empty(fixture.Desktop.BuildPrompt(fixture.Host.Scope, "A real later user message", false));
        Assert.DoesNotContain(await fixture.BuildNativePrompt(), message => message.Content.Contains("desktop-context:", StringComparison.Ordinal));
    }

    private sealed class Fixture : IAsyncDisposable
    {
        public TestHost Host { get; } = new();
        public Budget Budget { get; } = new();
        public Transport Transport { get; } = new();
        public DialogueContextLifetime Lifetime { get; } = new();
        public CompanionActivityCoordinator Coordinator { get; }
        public DesktopContextPlugin Desktop { get; }
        public PluginRuntime Runtime { get; }
        private AgentContextAssembler Assembler { get; }
        public List<string> Stages { get; } = [];
        public TaskCompletionSource Completed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        private Fixture(ITestOutputHelper output)
        {
            Coordinator = new(Host, new CompanionActivityModel(Transport, new RequestTokenMeter(), new Contexts()),
                Budget, Lifetime, TimeProvider.System, diagnostic =>
                {
                    Stages.Add(diagnostic.Stage);
                    output.WriteLine("activity {0:O} {1} {2} {3}", diagnostic.Timestamp, diagnostic.ActivityId, diagnostic.Stage, diagnostic.ErrorCode);
                    if (diagnostic.Stage == "completed") Completed.TrySetResult();
                });
            Host.Coordinator = Coordinator;
            Desktop = new(scope => Host.Scope == scope, diagnostics: diagnostic => output.WriteLine("context {0:O} {1} {2} {3}",
                diagnostic.Timestamp, diagnostic.Correlation, diagnostic.Stage, diagnostic.ErrorCode), contextSnapshot: Host.GetSnapshot);
            var catalog = PluginCatalog.Create([Desktop]);
            Runtime = new(catalog);
            Assembler = new(catalog, Runtime);
        }

        public static async Task<Fixture> Create(ITestOutputHelper output)
        {
            var fixture = new Fixture(output);
            Assert.True((await fixture.Runtime.StartAsync(CancellationToken.None)).Succeeded);
            return fixture;
        }

        public ComposedPrompt BuildOrdinaryPrompt()
        {
            const string userText = "A real later user message about the current focus interval.";
            return new PromptComposer().Compose(new PromptContext(new("mash", "fixture", "1", "default", "1", "1"),
                new PersonaBundle("mash", "fixture", "1", "1", "Synthetic companion persona.", []), [],
                Desktop.BuildPrompt(Host.Scope, userText, false), "", [], userText), ModelRouteKey.From(Host.Connection),
                PromptBudget.Resolve(Contexts.Limit(Host.Connection), 384));
        }

        public ValueTask<ImmutableArray<ModelMessage>> BuildNativePrompt()
        {
            var scope = Host.Scope;
            // This query belongs to the subsequent test user turn, never to the background activity.
            var request = new AgentContextRequest(scope, "followup-run", 1,
                new("followup-user-message", "synthetic-followup-fingerprint"), 24000, []);
            return Assembler.AssembleAsync(request,
                [new(ModelMessageRole.System, "Synthetic host rules.", []), new(ModelMessageRole.User, "A real later user message.", [])],
                messages => new RequestTokenMeter().Measure(ModelRouteKey.From(Host.Connection),
                    ChatRequest.CreateAgent(scope.RoleId, scope.ConversationId, messages)).InputTokens, CancellationToken.None);
        }

        public async Task AssertNoInterpretationInEitherPrompt()
        {
            Assert.DoesNotContain(BuildOrdinaryPrompt().Messages, message => message.Text.Contains("tentative_interpretation", StringComparison.Ordinal));
            Assert.DoesNotContain(await BuildNativePrompt(), message => message.Content.Contains("tentative_interpretation", StringComparison.Ordinal));
        }

        public async ValueTask DisposeAsync()
        {
            Transport.Release.TrySetResult();
            await Coordinator.DisposeAsync();
            await Runtime.DisposeAsync();
        }
    }

    private sealed class TestHost : ICompanionActivityHost
    {
        private readonly object _gate = new();
        private CompanionContextSnapshot _snapshot;
        private int _revision;
        public CompanionActivityCoordinator? Coordinator { get; set; }
        public bool BackgroundApproved { get; set; } = true;
        public bool SourceApproved { get; set; } = true;
        public ModelConnectionSettings Connection { get; } = new("openai", "https://fixture.invalid/v1", "fixture-model", maxOutputTokens: 384);
        public ToolScope Scope { get { lock (_gate) return _snapshot.Scope; } }

        public TestHost()
        {
            var now = DateTimeOffset.UtcNow;
            _snapshot = new(new("existing-conversation", "mash", "project-fixture"), "initial",
                [new("activity.focus", "initial", now, now.AddMinutes(10), "The focus timer has not yet reported a completed interval.")]);
        }

        public void PublishFocusCompleted()
        {
            lock (_gate)
            {
                var version = "focus-" + ++_revision;
                var now = DateTimeOffset.UtcNow;
                _snapshot = _snapshot with { Revision = version, Observations = [new("activity.focus", version, now,
                    now.AddMinutes(10), "The focus timer reported a completed interval. This does not establish task completion.")] };
            }
            Coordinator!.Invalidate();
            Coordinator.NotifyChanged("focus.completed");
        }

        public void RevokeSources()
        {
            lock (_gate) SourceApproved = false;
            Coordinator!.Invalidate();
        }

        public void ChangeScope(ToolScope scope)
        {
            lock (_gate) _snapshot = _snapshot with { Scope = scope, Revision = "scope-" + ++_revision };
            Coordinator!.Invalidate();
        }

        public CompanionActivityConfiguration? Capture()
        {
            lock (_gate) return BackgroundApproved && SourceApproved
                ? new(_snapshot, Connection, "policy-fixture", new()) : null;
        }

        public bool TryCommit(CompanionActivityConfiguration configuration, Action action)
        {
            lock (_gate)
            {
                if (!BackgroundApproved || !SourceApproved || configuration.Context.Scope != _snapshot.Scope
                    || configuration.Context.Revision != _snapshot.Revision || configuration.PolicyRevision != "policy-fixture"
                    || configuration.Context.Observations.Any(source => !_snapshot.Observations.Contains(source))) return false;
                action();
                return true;
            }
        }

        public CompanionContextSnapshot? GetSnapshot(ToolScope scope)
        {
            CompanionContextSnapshot snapshot;
            lock (_gate)
            {
                if (!SourceApproved || scope != _snapshot.Scope) return null;
                snapshot = _snapshot;
            }
            var note = Coordinator!.GetObservation(snapshot);
            lock (_gate)
                return !SourceApproved || _snapshot != snapshot ? null
                    : note is null ? snapshot : snapshot with { Observations = snapshot.Observations.Append(note).ToArray() };
        }
    }

    private sealed class Budget : ICompanionActivityBudget
    {
        public bool Allow { get; set; } = true;
        public List<CompanionActivityReservation> Charges { get; } = [];
        public bool TryReserve(CompanionActivityReservation reservation)
        {
            if (!Allow || Charges.Any(charge => charge.ActivityId == reservation.ActivityId)) return false;
            Charges.Add(reservation);
            return true;
        }
    }

    private sealed class Contexts : IModelContextResolver
    {
        public static ModelContextLimit Limit(ModelConnectionSettings settings) => new(ModelRouteKey.From(settings),
            32768, 2048, ContextLimitSource.Override, "synthetic-integration");
        public Task<ModelContextLimit> ResolveAsync(ModelConnectionSettings settings, CancellationToken cancellationToken)
            => Task.FromResult(Limit(settings));
    }

    private sealed class Transport : IChatProviderResolver
    {
        public bool HoldBeforeHandoff { get; set; }
        public bool HoldResponse { get; set; }
        public int GateCalls { get; private set; }
        public List<ChatRequest> Requests { get; } = [];
        public TaskCompletionSource Prepared { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Dispatched { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Cancelled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public IChatProvider Resolve() => throw new NotSupportedException();
        public IChatProvider Resolve(ModelConnectionSettings settings, IChatDispatchGate dispatchGate) => new Provider(this, settings, dispatchGate);

        private sealed class Provider(Transport owner, ModelConnectionSettings connection, IChatDispatchGate gate) : IChatProvider
        {
            public string ProviderId => connection.ProviderId;
            public string ModelId => connection.ModelId;
            public Task<IReadOnlyList<ProviderModel>> ListModelsAsync(CancellationToken cancellationToken)
                => Task.FromResult<IReadOnlyList<ProviderModel>>([]);
            public async IAsyncEnumerable<ChatStreamChunk> StreamAsync(ChatRequest request, [EnumeratorCancellation] CancellationToken token)
            {
                owner.Prepared.TrySetResult();
                if (owner.HoldBeforeHandoff) await owner.Release.Task;
                owner.GateCalls++;
                gate.Dispatch(request, ModelRouteKey.From(connection), () => { owner.Requests.Add(request); return true; }, token);
                using var registration = token.Register(() => owner.Cancelled.TrySetResult());
                owner.Dispatched.TrySetResult();
                if (owner.HoldResponse) await owner.Release.Task;
                yield return new(JsonSerializer.Serialize(new
                {
                    schema = 1, activity = request.Metadata["fgo_activity_id"],
                    interpretation = "A completed focus interval may be relevant to a later conversation; task completion remains unknown.",
                    sources = new[] { "s1" }
                }), true, "stop");
            }
        }
    }
}
