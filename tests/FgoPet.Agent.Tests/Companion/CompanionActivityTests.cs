using System.Net;
using System.Runtime.CompilerServices;
using System.Text.Encodings.Web;
using System.Text.Json;
using FgoPet.App.Dialogue;
using FgoPet.Core.Dialogue;
using FgoPet.Core.Settings;
using FgoPet.Extensibility;
using FgoPet.Infrastructure.Providers;
using FgoPet.Kernel.Companion;
using Xunit;

namespace FgoPet.Agent.Tests.Companion;

public sealed class CompanionActivityTests
{
    [Fact]
    public async Task Real_auxiliary_model_runs_without_a_user_message_and_projects_only_a_temporary_interpretation()
    {
        await using var fixture = new Fixture();
        fixture.Coordinator.NotifyChanged("focus.completed");
        fixture.Coordinator.NotifyChanged("focus.completed");
        await fixture.Coordinator.PulseAsync();

        var request = Assert.Single(fixture.Provider.Requests);
        Assert.All(request.ModelMessages, message => Assert.Equal(FgoPet.Kernel.Agent.ModelMessageRole.System, message.Role));
        Assert.Empty(request.Messages);
        Assert.Null(request.Tools);
        Assert.Equal("companion_activity", request.Metadata["fgo_auxiliary"]);
        Assert.True(Guid.TryParseExact(request.Metadata["fgo_activity_id"], "N", out _));
        var reservation = Assert.Single(fixture.Budget.Reservations);
        Assert.Equal(request.Metadata["fgo_activity_id"], reservation.ActivityId);
        Assert.Equal(fixture.Host.Current!.Context.Scope, reservation.Scope);
        var observation = Assert.IsType<CompanionObservation>(fixture.Coordinator.GetObservation(fixture.Snapshot));
        Assert.True(observation.IsInterpretation);
        Assert.Contains("tentative_not_user_confirmed", observation.Content);
        Assert.Contains("focus.state", observation.Content);
        Assert.Contains("v1", observation.Content);
        Assert.True(observation.ValidUntilUtc <= fixture.Clock.GetUtcNow().AddMinutes(15));
    }

    [Fact]
    public async Task Identical_revision_is_not_reprocessed_after_time_passes()
    {
        await using var fixture = new Fixture();
        fixture.Coordinator.NotifyChanged();
        await fixture.Coordinator.PulseAsync();
        fixture.Clock.Advance(TimeSpan.FromMinutes(10));
        fixture.Coordinator.NotifyChanged();
        await fixture.Coordinator.PulseAsync();
        Assert.Single(fixture.Provider.Requests);
        Assert.Single(fixture.Budget.Reservations);
    }

    [Fact]
    public async Task Started_worker_processes_a_host_event_without_any_conversation_send()
    {
        await using var fixture = new Fixture();
        fixture.Coordinator.Start(CancellationToken.None);
        fixture.Coordinator.NotifyChanged("focus.completed");
        // The first pulse may already have observed no work; the host clock supplies the next wake.
        fixture.Clock.Advance(TimeSpan.FromSeconds(5));
        await fixture.Completed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Single(fixture.Provider.Requests);
        Assert.NotNull(fixture.Coordinator.GetObservation(fixture.Snapshot));
    }

    [Fact]
    public async Task Cooldown_retains_only_the_latest_changed_revision_for_a_later_pulse()
    {
        await using var fixture = new Fixture();
        fixture.Host.Current = fixture.Host.Current! with { Limits = fixture.Host.Current!.Limits with { MinInterval = TimeSpan.FromMinutes(5) } };
        fixture.Coordinator.NotifyChanged();
        await fixture.Coordinator.PulseAsync();
        fixture.Change("v2");
        fixture.Coordinator.NotifyChanged();
        await fixture.Coordinator.PulseAsync();
        fixture.Clock.Advance(TimeSpan.FromMinutes(4));
        fixture.Change("v3");
        fixture.Coordinator.NotifyChanged();
        await fixture.Coordinator.PulseAsync();
        Assert.Single(fixture.Provider.Requests);
        fixture.Clock.Advance(TimeSpan.FromMinutes(1));
        await fixture.Coordinator.PulseAsync();
        Assert.Equal(2, fixture.Provider.Requests.Count);
        Assert.Contains("v3", fixture.Coordinator.GetObservation(fixture.Snapshot)!.Content);
    }

    [Fact]
    public async Task Changes_during_a_running_activity_are_coalesced_and_the_stale_result_is_discarded()
    {
        await using var fixture = new Fixture();
        fixture.Provider.Hold = true;
        fixture.Provider.IgnoreCancellation = true;
        fixture.Coordinator.NotifyChanged();
        var first = fixture.Coordinator.PulseAsync();
        await fixture.Provider.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        fixture.Change("v2");
        fixture.Coordinator.NotifyChanged();
        fixture.Change("v3");
        fixture.Coordinator.NotifyChanged();
        await fixture.Coordinator.PulseAsync(); // The existing activity still owns the serial slot.
        Assert.Single(fixture.Provider.Requests);
        fixture.Provider.Release.TrySetResult();
        await first;
        Assert.Null(fixture.Coordinator.GetObservation(fixture.Snapshot));
        await fixture.Coordinator.PulseAsync();
        Assert.Equal(2, fixture.Provider.Requests.Count);
        Assert.Contains("v3", fixture.Coordinator.GetObservation(fixture.Snapshot)!.Content);
        Assert.DoesNotContain("v2", fixture.Coordinator.GetObservation(fixture.Snapshot)!.Content);
    }

    [Fact]
    public async Task A_change_arriving_during_host_capture_is_not_lost_when_old_work_is_dequeued()
    {
        await using var fixture = new Fixture();
        fixture.Host.AfterCapture = () =>
        {
            fixture.Change("v2");
            fixture.Coordinator.NotifyChanged("focus.completed");
        };
        fixture.Coordinator.NotifyChanged();
        await fixture.Coordinator.PulseAsync();
        Assert.Empty(fixture.Provider.Requests);
        await fixture.Coordinator.PulseAsync();
        Assert.Single(fixture.Provider.Requests);
        Assert.Contains("v2", fixture.Coordinator.GetObservation(fixture.Snapshot)!.Content);
    }

    [Fact]
    public async Task Foreground_preempts_background_without_refunding_or_replaying_the_attempt()
    {
        await using var fixture = new Fixture();
        fixture.Provider.Hold = true;
        fixture.Coordinator.NotifyChanged();
        var pulse = fixture.Coordinator.PulseAsync();
        await fixture.Provider.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        using (fixture.Lifetime.Acquire(CancellationToken.None))
        {
            await fixture.Provider.Cancelled.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await pulse;
            Assert.Null(fixture.Coordinator.GetObservation(fixture.Snapshot));
        }
        fixture.Coordinator.NotifyChanged();
        await fixture.Coordinator.PulseAsync();
        Assert.Single(fixture.Budget.Reservations);
        Assert.Single(fixture.Provider.Requests);
    }

    [Fact]
    public async Task Foreground_busy_keeps_the_coalesced_work_for_a_later_pulse()
    {
        await using var fixture = new Fixture();
        fixture.Coordinator.NotifyChanged();
        using (fixture.Lifetime.Acquire(CancellationToken.None)) await fixture.Coordinator.PulseAsync();
        Assert.Empty(fixture.Provider.Requests);
        await fixture.Coordinator.PulseAsync();
        Assert.Single(fixture.Provider.Requests);
    }

    [Fact]
    public async Task Scope_invalidation_rejects_a_late_result_even_when_provider_ignores_cancellation()
    {
        await using var fixture = new Fixture();
        fixture.Provider.Hold = true;
        fixture.Provider.IgnoreCancellation = true;
        fixture.Coordinator.NotifyChanged();
        var pulse = fixture.Coordinator.PulseAsync();
        await fixture.Provider.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        fixture.Host.Current = fixture.Host.Current! with
        {
            Context = fixture.Snapshot with { Scope = new("conversation-2", "mash", "project-2") }
        };
        fixture.Coordinator.Invalidate();
        await fixture.Provider.Cancelled.Task.WaitAsync(TimeSpan.FromSeconds(5));
        fixture.Provider.Release.TrySetResult();
        await pulse;
        Assert.Null(fixture.Coordinator.GetObservation(fixture.Snapshot));
        Assert.Single(fixture.Budget.Reservations);
    }

    [Fact]
    public async Task Stop_cancels_and_drains_an_explicit_pulse_even_before_periodic_start()
    {
        var fixture = new Fixture();
        fixture.Provider.Hold = true;
        fixture.Provider.IgnoreCancellation = true;
        fixture.Coordinator.NotifyChanged();
        var pulse = fixture.Coordinator.PulseAsync();
        await fixture.Provider.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var stop = fixture.Coordinator.StopAsync();
        await fixture.Provider.Cancelled.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(stop.IsCompleted);
        fixture.Provider.Release.TrySetResult();
        await Task.WhenAll(pulse, stop).WaitAsync(TimeSpan.FromSeconds(5));
        fixture.Coordinator.NotifyChanged();
        await fixture.Coordinator.PulseAsync();
        Assert.Single(fixture.Provider.Requests);
        Assert.Null(fixture.Coordinator.GetObservation(fixture.Snapshot));
        await fixture.DisposeAsync();
    }

    [Fact]
    public async Task Denied_durable_budget_prevents_transport_dispatch()
    {
        await using var fixture = new Fixture();
        fixture.Budget.Allow = false;
        fixture.Coordinator.NotifyChanged();
        await fixture.Coordinator.PulseAsync();
        Assert.Equal(1, fixture.Budget.Attempts);
        Assert.Empty(fixture.Budget.Reservations);
        Assert.Empty(fixture.Provider.Requests);
        Assert.Equal("COMPANION_ACTIVITY_BUDGET_UNAVAILABLE", fixture.Coordinator.LastFailureCode);
    }

    [Fact]
    public async Task Full_input_budget_is_checked_before_any_quota_or_transport_handoff()
    {
        await using var fixture = new Fixture();
        fixture.Host.Current = fixture.Host.Current! with { Limits = fixture.Host.Current!.Limits with { MaxInputTokens = 64 } };
        fixture.Coordinator.NotifyChanged();
        await fixture.Coordinator.PulseAsync();
        Assert.Empty(fixture.Provider.Requests);
        Assert.Equal(0, fixture.Budget.Attempts);
        Assert.Equal("COMPANION_ACTIVITY_INPUT_LIMIT", fixture.Coordinator.LastFailureCode);
    }

    [Fact]
    public async Task Chinese_multisource_context_fits_the_real_default_meter_and_preserves_partial_read_provenance()
    {
        await using var fixture = new Fixture();
        SetLargeChineseSources(fixture);
        fixture.Provider.Response = request => Result(request, SubmittedSources(request).Take(2)
            .Select(source => source.GetProperty("alias").GetString()!).ToArray());
        fixture.Coordinator.NotifyChanged("focus.completed");
        await fixture.Coordinator.PulseAsync();

        var request = Assert.Single(fixture.Provider.Requests);
        var measured = new RequestTokenMeter().Measure(ModelRouteKey.From(fixture.Host.Current!.Connection), request);
        Assert.InRange(measured.InputTokens, 1, 4096);
        Assert.Equal(384, request.MaxOutputTokens);
        var supplied = SubmittedSources(request);
        Assert.Contains(supplied, source => source.GetProperty("SourceId").GetString() == "desktop.window");
        Assert.Contains(supplied, source => source.GetProperty("SourceId").GetString() == "activity.focus");
        Assert.Contains("\"status\":\"Completed\"", Assert.Single(supplied, source => source.GetProperty("SourceId").GetString()
            == "activity.focus").GetProperty("text").GetString());
        Assert.Contains("可见中文片段", Assert.Single(supplied, source => source.GetProperty("SourceId").GetString()
            == "desktop.window").GetProperty("text").GetString());
        Assert.All(supplied, source =>
        {
            var original = Assert.Single(fixture.Snapshot.Observations, item => item.SourceId == source.GetProperty("SourceId").GetString());
            var excerpt = source.GetProperty("text").GetString()!;
            Assert.Equal(original.Version, source.GetProperty("Version").GetString());
            Assert.Equal(original.Content.Length, source.GetProperty("totalCharacters").GetInt32());
            Assert.Equal(excerpt.Length, source.GetProperty("readCharacters").GetInt32());
            Assert.StartsWith(excerpt, original.Content);
            Assert.Equal(excerpt.Length == original.Content.Length ? "complete_observation" : "prefix_excerpt",
                source.GetProperty("readCoverage").GetString());
        });
        Assert.Contains("可见", request.ModelMessages[1].Content);
        var observation = Assert.IsType<CompanionObservation>(fixture.Coordinator.GetObservation(fixture.Snapshot));
        using var note = JsonDocument.Parse(observation.Content);
        Assert.All(note.RootElement.GetProperty("sources").EnumerateArray(), source =>
        {
            var read = source.GetProperty("readCharacters").GetInt32();
            var total = source.GetProperty("totalCharacters").GetInt32();
            Assert.Equal(read == total ? "complete_observation" : "prefix_excerpt", source.GetProperty("readCoverage").GetString());
            Assert.InRange(read, 1, total);
        });
        Assert.Equal("prefix_excerpt", Assert.Single(note.RootElement.GetProperty("sources").EnumerateArray(),
            source => source.GetProperty("SourceId").GetString() == "desktop.window").GetProperty("readCoverage").GetString());
        Assert.Contains("omitted text was not reviewed", observation.Content);
        Assert.Equal(4096, Assert.Single(fixture.Budget.Reservations).InputTokens);
    }

    [Fact]
    public async Task Alias_of_a_budget_omitted_source_is_rejected_even_when_the_host_knows_that_source()
    {
        await using var fixture = new Fixture();
        SetLargeChineseSources(fixture);
        string? omittedAlias = null;
        fixture.Provider.Response = request =>
        {
            var submitted = SubmittedSources(request).Select(source => source.GetProperty("alias").GetString()).ToHashSet();
            omittedAlias = Enumerable.Range(1, fixture.Snapshot.Observations.Count).Select(index => "s" + index)
                .First(alias => !submitted.Contains(alias));
            return Result(request, [omittedAlias]);
        };
        fixture.Coordinator.NotifyChanged();
        await fixture.Coordinator.PulseAsync();

        Assert.NotNull(omittedAlias);
        Assert.Single(fixture.Provider.Requests);
        Assert.Single(fixture.Budget.Reservations);
        Assert.Null(fixture.Coordinator.GetObservation(fixture.Snapshot));
        Assert.Equal("COMPANION_ACTIVITY_INVALID_RESULT", fixture.Coordinator.LastFailureCode);
    }

    [Fact]
    public async Task A_model_adapter_without_read_attestation_projects_unknown_coverage_instead_of_complete_read()
    {
        await using var fixture = new Fixture(omitCoverage: true);
        fixture.Coordinator.NotifyChanged();
        await fixture.Coordinator.PulseAsync();
        var observation = Assert.IsType<CompanionObservation>(fixture.Coordinator.GetObservation(fixture.Snapshot));
        using var note = JsonDocument.Parse(observation.Content);
        var source = Assert.Single(note.RootElement.GetProperty("sources").EnumerateArray());
        Assert.Equal("unknown", source.GetProperty("readCoverage").GetString());
        Assert.Equal(JsonValueKind.Null, source.GetProperty("readCharacters").ValueKind);
    }

    private static void SetLargeChineseSources(Fixture fixture)
    {
        var now = fixture.Clock.GetUtcNow();
        var sourceIds = new[] { "activity.todos", "activity.reminders", "desktop.applications", "time.period", "activity.focus", "desktop.window" };
        fixture.Host.Current = fixture.Host.Current! with
        {
            Context = fixture.Snapshot with
            {
                Observations = sourceIds.Select((source, index) => new CompanionObservation(source, new string((char)('a' + index), 64),
                    now, now.AddMinutes(2), JsonSerializer.Serialize<object>(source switch
                    {
                        "activity.focus" => new { status = "Completed", phase = "Work", CurrentCycle = 1, TotalCycles = 4,
                            IsCurrent = false, SessionId = "9a54c1f3a492434b83097f0fa591e476", meaning = "Timer state only; task completion remains unknown." },
                        "desktop.window" => new { application = "synthetic-editor", visibleTextExcerpt = "可见中文片段" + new string('文', 1200),
                            window = "合成窗口", partial = true },
                        _ => new { description = "可见的合成来源，仅表示来源状态，不推断用户任务已完成。" + new string('文', 1200) }
                    }, new JsonSerializerOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))).ToArray()
            }
        };
    }

    private static JsonElement[] SubmittedSources(ChatRequest request)
    {
        var wrapped = request.ModelMessages[1].Content;
        var data = WebUtility.HtmlDecode(wrapped[(wrapped.IndexOf('{'))..(wrapped.LastIndexOf('}') + 1)]);
        using var document = JsonDocument.Parse(data);
        return document.RootElement.GetProperty("observations").EnumerateArray().Select(source => source.Clone()).ToArray();
    }

    private static string Result(ChatRequest request, string[] aliases) => JsonSerializer.Serialize(new
    {
        schema = 1, activity = request.Metadata["fgo_activity_id"], interpretation = "A tentative connection based only on the supplied excerpt.", sources = aliases
    });

    [Fact]
    public async Task Policy_change_after_async_preparation_is_rejected_at_the_real_handoff()
    {
        await using var fixture = new Fixture();
        fixture.Provider.HoldBeforeDispatch = true;
        fixture.Coordinator.NotifyChanged();
        var pulse = fixture.Coordinator.PulseAsync();
        await fixture.Provider.Prepared.Task.WaitAsync(TimeSpan.FromSeconds(5));
        fixture.Host.Current = fixture.Host.Current! with { PolicyRevision = "revoked" };
        fixture.Provider.ReleaseBeforeDispatch.TrySetResult();
        await pulse;
        Assert.Empty(fixture.Provider.Requests);
        Assert.Equal(0, fixture.Budget.Attempts);
    }

    [Fact]
    public async Task Timeout_cancels_the_real_request_and_keeps_its_charge()
    {
        await using var fixture = new Fixture();
        fixture.Provider.Hold = true;
        fixture.Coordinator.NotifyChanged();
        var pulse = fixture.Coordinator.PulseAsync();
        await fixture.Provider.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        fixture.Clock.Advance(TimeSpan.FromSeconds(31));
        await pulse.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(fixture.Provider.Cancelled.Task.IsCompleted);
        Assert.Single(fixture.Budget.Reservations);
        Assert.Null(fixture.Coordinator.GetObservation(fixture.Snapshot));
    }

    [Fact]
    public async Task No_change_result_and_expired_interpretations_do_not_enter_future_context()
    {
        await using var fixture = new Fixture();
        fixture.Provider.Response = request => JsonSerializer.Serialize(new
        {
            schema = 1, activity = request.Metadata["fgo_activity_id"], interpretation = (string?)null, sources = Array.Empty<string>()
        });
        fixture.Coordinator.NotifyChanged();
        await fixture.Coordinator.PulseAsync();
        Assert.Null(fixture.Coordinator.GetObservation(fixture.Snapshot));
        fixture.Change("v2");
        fixture.Provider.Response = null;
        fixture.Coordinator.NotifyChanged();
        await fixture.Coordinator.PulseAsync();
        Assert.NotNull(fixture.Coordinator.GetObservation(fixture.Snapshot));
        fixture.Clock.Advance(TimeSpan.FromMinutes(16));
        Assert.Null(fixture.Coordinator.GetObservation(fixture.Snapshot));
    }

    [Fact]
    public async Task New_source_revision_immediately_hides_the_previous_interpretation()
    {
        await using var fixture = new Fixture();
        fixture.Coordinator.NotifyChanged();
        await fixture.Coordinator.PulseAsync();
        Assert.NotNull(fixture.Coordinator.GetObservation(fixture.Snapshot));
        fixture.Change("v2");
        Assert.Null(fixture.Coordinator.GetObservation(fixture.Snapshot));
    }

    [Fact]
    public async Task Prior_model_interpretations_are_not_recycled_as_factual_observations()
    {
        await using var fixture = new Fixture();
        fixture.Host.Current = fixture.Host.Current! with
        {
            Context = fixture.Snapshot with { Observations = [fixture.Snapshot.Observations[0],
                new("activity.old", "old", fixture.Clock.GetUtcNow(), fixture.Clock.GetUtcNow().AddHours(1),
                    "UNSUPPORTED_PREVIOUS_MODEL_GUESS", true)] }
        };
        fixture.Coordinator.NotifyChanged();
        await fixture.Coordinator.PulseAsync();
        var request = Assert.Single(fixture.Provider.Requests);
        Assert.DoesNotContain("UNSUPPORTED_PREVIOUS_MODEL_GUESS", string.Join("", request.ModelMessages.Select(message => message.Content)));
    }

    [Theory]
    [InlineData("extra_field")]
    [InlineData("duplicate_field")]
    [InlineData("wrong_activity")]
    [InlineData("unknown_source")]
    [InlineData("duplicate_source")]
    [InlineData("missing_source")]
    [InlineData("tool_call")]
    public async Task Invalid_model_results_never_become_context(string kind)
    {
        await using var fixture = new Fixture();
        fixture.Provider.ToolCall = kind == "tool_call";
        fixture.Provider.Response = request =>
        {
            var id = request.Metadata["fgo_activity_id"];
            var result = $"{{\"schema\":1,\"activity\":\"{id}\",\"interpretation\":\"A tentative connection\",\"sources\":[\"s1\"]}}";
            return kind switch
            {
                "extra_field" => result[..^1] + ",\"command\":\"focus.start\"}",
                "duplicate_field" => result[..^1] + ",\"schema\":1}",
                "wrong_activity" => result.Replace(id, "another-activity", StringComparison.Ordinal),
                "unknown_source" => result.Replace("s1", "s99", StringComparison.Ordinal),
                "duplicate_source" => result.Replace("[\"s1\"]", "[\"s1\",\"s1\"]", StringComparison.Ordinal),
                "missing_source" => result.Replace("[\"s1\"]", "[]", StringComparison.Ordinal),
                _ => result
            };
        };
        fixture.Coordinator.NotifyChanged();
        await fixture.Coordinator.PulseAsync();
        Assert.Single(fixture.Budget.Reservations);
        Assert.Null(fixture.Coordinator.GetObservation(fixture.Snapshot));
        Assert.NotNull(fixture.Coordinator.LastFailureCode);
    }

    private sealed class Fixture : IAsyncDisposable
    {
        public ManualClock Clock { get; } = new();
        public TestHost Host { get; }
        public TestBudget Budget { get; } = new();
        public SyntheticProviders Provider { get; } = new();
        public DialogueContextLifetime Lifetime { get; } = new();
        public CompanionActivityCoordinator Coordinator { get; }
        public TaskCompletionSource Completed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public CompanionContextSnapshot Snapshot => Host.Current!.Context;

        public Fixture(bool omitCoverage = false)
        {
            var now = Clock.GetUtcNow();
            Host = new(new(new(new("conversation-1", "mash", "project-1"), "v1",
                [new("focus.state", "v1", now, now.AddHours(1), "A focus interval ended; task completion is unknown.")]),
                new("openai", "https://example.invalid/v1", "fixture-model", maxOutputTokens: 512), "policy-1",
                new() { MinInterval = TimeSpan.Zero }));
            ICompanionActivityModel model = new CompanionActivityModel(Provider, new RequestTokenMeter(), new Contexts());
            if (omitCoverage) model = new UnattestedModel(model);
            Coordinator = new(Host, model,
                Budget, Lifetime, Clock, diagnostic =>
                {
                    if (diagnostic.Stage == "completed") Completed.TrySetResult();
                });
        }

        public void Change(string version)
        {
            Host.Current = Host.Current! with { Context = Snapshot with { Revision = version,
                Observations = [Snapshot.Observations[0] with { Version = version }] } };
        }
        public ValueTask DisposeAsync() => Coordinator.DisposeAsync();
    }

    private sealed class UnattestedModel(ICompanionActivityModel inner) : ICompanionActivityModel
    {
        public async Task<CompanionActivityJudgment> EvaluateAsync(CompanionActivityRequest request, IChatDispatchGate dispatchGate,
            Action<CancellationToken> ensureCurrent, CancellationToken cancellationToken)
            => (await inner.EvaluateAsync(request, dispatchGate, ensureCurrent, cancellationToken)) with { SourceCoverage = null };
    }

    private sealed class TestHost(CompanionActivityConfiguration initial) : ICompanionActivityHost
    {
        private readonly object _gate = new();
        private CompanionActivityConfiguration? _current = initial;
        public Action? AfterCapture { get; set; }
        public CompanionActivityConfiguration? Current { get { lock (_gate) return _current; } set { lock (_gate) _current = value; } }
        public CompanionActivityConfiguration? Capture()
        {
            var captured = Current;
            var after = AfterCapture;
            AfterCapture = null;
            after?.Invoke();
            return captured;
        }
        public bool TryCommit(CompanionActivityConfiguration configuration, Action action)
        {
            lock (_gate)
            {
                if (_current is null || configuration.Context.Scope != _current.Context.Scope
                    || configuration.Context.Revision != _current.Context.Revision || configuration.PolicyRevision != _current.PolicyRevision
                    || configuration.Limits != _current.Limits
                    || ModelRouteKey.From(configuration.Connection) != ModelRouteKey.From(_current.Connection)
                    || configuration.Context.Observations.Any(source => !_current.Context.Observations.Any(current => current == source)))
                    return false;
                action();
                return true;
            }
        }
    }

    private sealed class TestBudget : ICompanionActivityBudget
    {
        public bool Allow { get; set; } = true;
        public int Attempts { get; private set; }
        public List<CompanionActivityReservation> Reservations { get; } = [];
        public bool TryReserve(CompanionActivityReservation reservation)
        {
            Attempts++;
            if (!Allow || Reservations.Any(item => item.ActivityId == reservation.ActivityId)) return false;
            Reservations.Add(reservation);
            return true;
        }
    }

    private sealed class Contexts : IModelContextResolver
    {
        public Task<ModelContextLimit> ResolveAsync(ModelConnectionSettings settings, CancellationToken cancellationToken)
            => Task.FromResult(new ModelContextLimit(ModelRouteKey.From(settings), 32768, 2048, ContextLimitSource.Override, "fixture"));
    }

    private sealed class SyntheticProviders : IChatProviderResolver
    {
        public List<ChatRequest> Requests { get; } = [];
        public bool Hold { get; set; }
        public bool IgnoreCancellation { get; set; }
        public bool HoldBeforeDispatch { get; set; }
        public bool ToolCall { get; set; }
        public Func<ChatRequest, string>? Response { get; set; }
        public TaskCompletionSource<ChatRequest> Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Prepared { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Cancelled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseBeforeDispatch { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public IChatProvider Resolve() => throw new NotSupportedException();
        public IChatProvider Resolve(ModelConnectionSettings settings) => throw new NotSupportedException();
        public IChatProvider Resolve(ModelConnectionSettings settings, IChatDispatchGate dispatchGate) => new Provider(this, settings, dispatchGate);

        private sealed class Provider(SyntheticProviders owner, ModelConnectionSettings settings, IChatDispatchGate gate) : IChatProvider
        {
            public string ProviderId => settings.ProviderId;
            public string ModelId => settings.ModelId;
            public Task<IReadOnlyList<ProviderModel>> ListModelsAsync(CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<ProviderModel>>([]);
            public async IAsyncEnumerable<ChatStreamChunk> StreamAsync(ChatRequest request, [EnumeratorCancellation] CancellationToken cancellationToken)
            {
                owner.Prepared.TrySetResult();
                if (owner.HoldBeforeDispatch) await owner.ReleaseBeforeDispatch.Task.WaitAsync(cancellationToken);
                gate.Dispatch(request, ModelRouteKey.From(settings), () => { owner.Requests.Add(request); return true; }, cancellationToken);
                using var registration = cancellationToken.Register(() => owner.Cancelled.TrySetResult());
                owner.Started.TrySetResult(request);
                if (owner.Hold)
                {
                    if (owner.IgnoreCancellation) await owner.Release.Task;
                    else
                    {
                        try { await owner.Release.Task.WaitAsync(cancellationToken); }
                        catch (OperationCanceledException)
                        {
                            // Cancellation may resume this iterator before its earlier registration runs.
                            owner.Cancelled.TrySetResult();
                            throw;
                        }
                    }
                }
                if (owner.ToolCall)
                {
                    yield return new("", false, null, new(0, "call-1", "focus.start", "{}"));
                    yield return new("", true, "tool_calls");
                    yield break;
                }
                var response = owner.Response?.Invoke(request) ?? JsonSerializer.Serialize(new
                {
                    schema = 1, activity = request.Metadata["fgo_activity_id"],
                    interpretation = "The focus interval ended; later relevant conversation can acknowledge the interval without assuming the task is done.",
                    sources = new[] { "s1" }
                });
                yield return new(response, true, "stop", Usage: new(256, 64));
            }
        }
    }

    private sealed class ManualClock : TimeProvider
    {
        private readonly object _gate = new();
        private readonly List<ManualTimer> _timers = [];
        private readonly DateTimeOffset _origin = new(2026, 10, 24, 0, 0, 0, TimeSpan.Zero);
        private long _ticks;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override DateTimeOffset GetUtcNow() { lock (_gate) return _origin.AddTicks(_ticks); }
        public override long GetTimestamp() { lock (_gate) return _ticks; }
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            lock (_gate)
            {
                var timer = new ManualTimer(this, callback, state);
                _timers.Add(timer);
                timer.Change(dueTime, period);
                return timer;
            }
        }
        public void Advance(TimeSpan interval)
        {
            List<ManualTimer> due;
            lock (_gate)
            {
                _ticks += interval.Ticks;
                due = _timers.Where(timer => !timer.Disposed && timer.Next <= _ticks).ToList();
                foreach (var timer in due) timer.Next = timer.Period == Timeout.InfiniteTimeSpan ? long.MaxValue : _ticks + timer.Period.Ticks;
            }
            foreach (var timer in due) timer.Fire();
        }
        private sealed class ManualTimer(ManualClock owner, TimerCallback callback, object? state) : ITimer
        {
            public bool Disposed { get; private set; }
            public long Next { get; set; }
            public TimeSpan Period { get; private set; }
            public bool Change(TimeSpan dueTime, TimeSpan period)
            {
                lock (owner._gate)
                {
                    if (Disposed) return false;
                    Next = dueTime == Timeout.InfiniteTimeSpan ? long.MaxValue : owner._ticks + dueTime.Ticks;
                    Period = period;
                    return true;
                }
            }
            public void Fire() { if (!Disposed) callback(state); }
            public void Dispose() { lock (owner._gate) { Disposed = true; owner._timers.Remove(this); } }
            public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
        }
    }
}
