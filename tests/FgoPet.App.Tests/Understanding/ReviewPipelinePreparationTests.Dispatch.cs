using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FgoPet.Core.Dialogue;
using FgoPet.Kernel.Agent;
using FgoPet.Understanding.Contracts;
using FgoPet.Validation.Understanding;
using Xunit;

namespace FgoPet.App.Tests.Understanding;

public sealed partial class ReviewPipelinePreparationTests
{
    [Fact]
    public async Task Dispatch_charge_is_durable_before_handoff_and_duplicate_never_starts()
    {
        using var f = new DispatchFixture(); var calls = 0;
        var binding = f.Binding(); var permit = f.Authority.Issue(f.Ledger, binding);
        Assert.Equal(7, await permit.RunAsync(binding, _ => { }, _ =>
        {
            var reopened = f.Reopen(); var row = Assert.Single(reopened.Read());
            Assert.Equal("Reserved", row.State); calls++;
            return Task.FromResult(7);
        }, _ => new(17, 5)));
        var completed = Assert.Single(f.Reopen().Read());
        Assert.Equal("Completed", completed.State); Assert.Equal(17, completed.InputTokens); Assert.Equal(5, completed.OutputTokens);
        await Assert.ThrowsAsync<AgentProtocolException>(() => f.Run(binding, _ => { calls++; return Task.FromResult(7); }));
        await Assert.ThrowsAsync<AgentProtocolException>(() => permit.RunAsync(binding, _ => { }, _ => { calls++; return Task.FromResult(7); }, _ => new(17, 5)));
        Assert.Equal(1, calls); Assert.Single(f.Ledger.Read());
    }

    [Theory]
    [InlineData("timeout")]
    [InlineData("cancel")]
    [InlineData("no-usage")]
    public async Task Dispatch_unknown_or_cancelled_attempt_is_not_refunded(string failure)
    {
        using var f = new DispatchFixture(total: 1); var calls = 0; var binding = f.Binding();
        var primary = failure == "cancel" ? (Exception)new OperationCanceledException("PRIVATE_CANCEL") : new TimeoutException("PRIVATE_TIMEOUT");
        var task = f.Run(binding, _ => { calls++; return failure == "no-usage" ? Task.FromResult(7) : Task.FromException<int>(primary); }, knownUsage: false);
        if (failure == "no-usage") Assert.Equal(7, await task);
        else Assert.Same(primary, await Record.ExceptionAsync(async () => await task));
        var row = Assert.Single(f.Reopen().Read()); Assert.Equal("Unknown", row.State); Assert.Null(row.InputTokens);
        await Assert.ThrowsAsync<AgentProtocolException>(() => f.Run(f.Binding("second"), _ => { calls++; return Task.FromResult(7); }));
        Assert.Equal(1, calls); Assert.Single(f.Ledger.Read());
    }

    [Fact]
    public async Task Dispatch_reopening_and_recovery_preserve_spent_budget_and_freeze_limits()
    {
        using var f = new DispatchFixture(total: 1); var binding = f.Binding(); f.Ledger.Reserve(binding);
        var reopened = f.Reopen(); reopened.RecoverInterrupted(); reopened.RecoverInterrupted();
        Assert.Equal("Unknown", Assert.Single(reopened.Read()).State);
        Assert.Throws<AgentProtocolException>(() => reopened.Reserve(f.Binding("after-restart")));
        Assert.Throws<AgentProtocolException>(() => f.Reopen(total: 2));
        Assert.Throws<AgentProtocolException>(() => reopened.Register(DispatchHash("batch"), DispatchHash("trial"), 4, 3));
        var authority = new ReviewDispatchAuthorityProbe(); var calls = 0;
        Assert.Throws<AgentProtocolException>(() => authority.Issue(reopened, f.Binding("after-restart")));
        await Assert.ThrowsAsync<AgentProtocolException>(() => f.Run(f.Binding("still-full"), _ => { calls++; return Task.FromResult(7); }));
        Assert.Equal(0, calls); Assert.Single(reopened.Read());
    }

    [Fact]
    public void Dispatch_trial_batch_and_task_caps_are_independent_and_atomic()
    {
        using var f = new DispatchFixture(total: 3, batch: 2, trial: 1);
        f.Ledger.Register(DispatchHash("batch"), DispatchHash("trial-b"), 2, 1);
        f.Ledger.Register(DispatchHash("batch-b"), DispatchHash("trial-c"), 2, 1);
        f.Ledger.Register(DispatchHash("batch-b"), DispatchHash("trial-d"), 2, 1);
        f.Ledger.Reserve(f.Binding("one"));
        Assert.Throws<AgentProtocolException>(() => f.Ledger.Reserve(f.Binding("trial-over")));
        f.Ledger.Reserve(f.Binding("two", trial: "trial-b"));
        f.Ledger.Register(DispatchHash("batch"), DispatchHash("trial-unused"), 2, 1);
        Assert.Throws<AgentProtocolException>(() => f.Ledger.Reserve(f.Binding("batch-over", trial: "trial-unused")));
        f.Ledger.Reserve(f.Binding("three", batch: "batch-b", trial: "trial-c"));
        Assert.Throws<AgentProtocolException>(() => f.Ledger.Reserve(f.Binding("total-over", batch: "batch-b", trial: "trial-d")));
        Assert.Equal(3, f.Reopen().Read().Count);
    }

    [Fact]
    public async Task Dispatch_parallel_connections_cannot_overspend_or_double_charge_a_request()
    {
        using var f = new DispatchFixture(total: 3, batch: 3, trial: 3);
        using var ready = new Barrier(8);
        var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(i => Task.Run(() =>
        {
            var ledger = f.Reopen(); Assert.True(ready.SignalAndWait(TimeSpan.FromSeconds(15)));
            try { ledger.Reserve(f.Binding(i.ToString())); return true; }
            catch (AgentProtocolException) { return false; }
        })));
        Assert.Equal(3, results.Count(x => x)); Assert.Equal(3, f.Reopen().Read().Count);
        using var other = new DispatchFixture(total: 3, batch: 3, trial: 3);
        var duplicate = other.Binding();
        results = await Task.WhenAll(Enumerable.Range(0, 2).Select(_ => Task.Run(() =>
        {
            try { other.Reopen().Reserve(duplicate); return true; }
            catch (AgentProtocolException) { return false; }
        })));
        Assert.Single(results.Where(x => x)); Assert.Single(other.Ledger.Read());
    }

    [Fact]
    public async Task Dispatch_default_off_and_regrant_do_not_revive_a_permit()
    {
        using var f = new DispatchFixture(); var binding = f.Binding(); var calls = 0;
        var off = new ReviewDispatchAuthorityProbe(); Assert.Throws<AgentProtocolException>(() => off.Issue(f.Ledger, binding));
        var permit = f.Authority.Issue(f.Ledger, binding);
        f.Authority.Configure(f.Context, false); f.Authority.Configure(f.Context, true);
        await Assert.ThrowsAsync<AgentProtocolException>(() => permit.RunAsync(binding, _ => { }, _ => { calls++; return Task.FromResult(7); }, _ => new(17, 5)));
        Assert.Equal(0, calls); Assert.Empty(f.Ledger.Read());
    }

    [Theory]
    [InlineData("request")]
    [InlineData("operation")]
    [InlineData("input")]
    [InlineData("scope")]
    [InlineData("route")]
    [InlineData("generation")]
    public async Task Dispatch_permit_is_bound_to_the_exact_request_and_context(string changed)
    {
        using var f = new DispatchFixture(); var binding = f.Binding(); var calls = 0;
        var permit = f.Authority.Issue(f.Ledger, binding);
        var actual = changed switch
        {
            "request" => binding with { RequestHash = DispatchHash("other-request") },
            "operation" => binding with { OperationHash = DispatchHash("other-operation") },
            "input" => binding with { InputHash = DispatchHash("other-body") },
            "scope" => binding with { Context = f.Context with { ScopeHash = DispatchHash("other-scope") } },
            "route" => binding with { Context = f.Context with { RouteHash = DispatchHash("other-route") } },
            _ => binding with { Context = f.Context with { PolicyGeneration = 2 } }
        };
        await Assert.ThrowsAsync<AgentProtocolException>(() => permit.RunAsync(actual, _ => { }, _ => { calls++; return Task.FromResult(7); }, _ => new(17, 5)));
        Assert.Equal(0, calls); Assert.Empty(f.Ledger.Read());
    }

    [Fact]
    public async Task Dispatch_precancellation_keeps_original_token_and_does_not_charge()
    {
        using var f = new DispatchFixture(); using var cancel = new CancellationTokenSource(); cancel.Cancel();
        var binding = f.Binding(); var permit = f.Authority.Issue(f.Ledger, binding); var calls = 0;
        var task = permit.RunAsync(binding, _ => { }, _ => { calls++; return Task.FromResult(7); }, _ => new(17, 5), cancel.Token);
        var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await task);
        Assert.Equal(cancel.Token, error.CancellationToken); Assert.True(task.IsCanceled);
        Assert.Equal(0, calls); Assert.Empty(f.Ledger.Read());
    }

    [Fact]
    public async Task Dispatch_revocation_during_last_diagnostic_wins_before_charge_and_handoff()
    {
        using var f = new DispatchFixture(); using var ready = new ManualResetEventSlim(); using var release = new ManualResetEventSlim();
        var authority = new ReviewDispatchAuthorityProbe(d =>
        {
            if (d.Stage != "dispatch_received") return;
            ready.Set(); Assert.True(release.Wait(TimeSpan.FromSeconds(15)));
        });
        authority.Configure(f.Context, true); var binding = f.Binding(); var permit = authority.Issue(f.Ledger, binding); var calls = 0;
        var task = Task.Run(() => permit.RunAsync(binding, _ => { }, _ => { Interlocked.Increment(ref calls); return Task.FromResult(7); }, _ => new(17, 5)));
        try { Assert.True(ready.Wait(TimeSpan.FromSeconds(15))); authority.Configure(f.Context, false); }
        finally { release.Set(); }
        await Assert.ThrowsAsync<AgentProtocolException>(async () => await task);
        Assert.Equal(0, calls); Assert.Empty(f.Ledger.Read());
    }

    [Fact]
    public async Task Dispatch_revocation_inside_final_fence_is_rechecked_before_handoff()
    {
        using var f = new DispatchFixture(); var binding = f.Binding(); var calls = 0; var permit = f.Authority.Issue(f.Ledger, binding);
        await Assert.ThrowsAsync<AgentProtocolException>(() => permit.RunAsync(binding,
            _ => f.Authority.Configure(f.Context, false), _ => { calls++; return Task.FromResult(7); }, _ => new(17, 5)));
        Assert.Equal(0, calls); Assert.Empty(f.Ledger.Read());
    }

    [Fact]
    public async Task Dispatch_winning_handoff_is_counted_then_revocation_cancels_and_blocks_result()
    {
        using var f = new DispatchFixture(total: 1); var binding = f.Binding(); var permit = f.Authority.Issue(f.Ledger, binding);
        var started = new TaskCompletionSource<CancellationToken>(TaskCreationOptions.RunContinuationsAsynchronously);
        var finish = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously); var calls = 0;
        var task = permit.RunAsync(binding, _ => { }, token => { calls++; started.SetResult(token); return finish.Task; }, _ => new(17, 5));
        var active = await started.Task.WaitAsync(TimeSpan.FromSeconds(15));
        Assert.Equal("Reserved", Assert.Single(f.Reopen().Read()).State);
        f.Authority.Configure(f.Context, false); Assert.True(active.IsCancellationRequested);
        finish.SetResult(7); await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await task);
        Assert.Equal(1, calls); Assert.Single(f.Ledger.Read());
        f.Authority.Configure(f.Context, true);
        await Assert.ThrowsAsync<AgentProtocolException>(() => f.Run(f.Binding("second"), _ => { calls++; return Task.FromResult(7); }));
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task Dispatch_synchronous_handoff_failure_preserves_exception_and_spends_once()
    {
        using var f = new DispatchFixture(); var binding = f.Binding(); var primary = new InvalidOperationException("PRIVATE_START");
        var error = await Record.ExceptionAsync(() => f.Run(binding, _ => throw primary));
        Assert.Same(primary, error); Assert.Equal("Unknown", Assert.Single(f.Reopen().Read()).State);
    }

    [Fact]
    public async Task Dispatch_cancellation_callback_failure_cannot_undo_revocation_or_replace_primary()
    {
        using var f = new DispatchFixture(); var binding = f.Binding(); CancellationTokenRegistration registration = default;
        var finish = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var primary = new OperationCanceledException("PRIVATE_PRIMARY");
        var task = f.Run(binding, token => { registration = token.Register(() => throw new InvalidOperationException("PRIVATE_CALLBACK")); return finish.Task; });
        try
        {
            f.Authority.Configure(f.Context, false); finish.SetException(primary);
            Assert.Same(primary, await Record.ExceptionAsync(async () => await task));
            Assert.Equal("Unknown", Assert.Single(f.Ledger.Read()).State);
            Assert.Throws<AgentProtocolException>(() => f.Authority.Issue(f.Ledger, f.Binding("blocked")));
        }
        finally { registration.Dispose(); }
    }

    [Fact]
    public async Task Dispatch_budget_rejection_happens_before_existing_exchange_provider_entry()
    {
        using var f = new Fixture(); f.Allow(); using var dispatch = new DispatchFixture(total: 1);
        dispatch.Ledger.Reserve(dispatch.Binding("already-spent"));
        var provider = new ReviewProvider { Frames = request => ReviewFrames(request) };
        f.ProduceAsync = async (request, token) =>
        {
            var binding = dispatch.Binding("exchange") with { InputHash = ReviewModelExchangeProbe.RequestBinding(request) };
            var permit = dispatch.Authority.Issue(dispatch.Ledger, binding);
            return (await permit.RunAsync(binding, request.EnsureCurrent,
                ct => new ReviewModelExchangeProbe(provider).RunAsync("PRIVATE_OPERATION", request, ct), result => result.Usage, token)).Proposals;
        };
        var receipt = await f.Run(); Assert.All(receipt.Items, item => Assert.Equal(UnderstandingOutcome.Failed, item.Outcome));
        Assert.Equal(0, provider.Calls); Assert.Equal(0, provider.Closed); Assert.Empty(f.Owner.List(f.Scope, true));
        Assert.Single(dispatch.Ledger.Read());
    }

    [Fact]
    public async Task Dispatch_existing_exchange_remains_pending_with_known_usage_and_one_stream_close()
    {
        using var f = new Fixture(); f.Allow(); using var dispatch = new DispatchFixture();
        var provider = new ReviewProvider { Frames = request => ReviewFrames(request) };
        f.ProduceAsync = async (request, token) =>
        {
            var binding = dispatch.Binding() with { InputHash = ReviewModelExchangeProbe.RequestBinding(request) };
            var permit = dispatch.Authority.Issue(dispatch.Ledger, binding);
            return (await permit.RunAsync(binding, request.EnsureCurrent,
                ct => new ReviewModelExchangeProbe(provider).RunAsync("PRIVATE_OPERATION", request, ct), result => result.Usage, token)).Proposals;
        };
        var receipt = await f.Run(); Assert.All(receipt.Items, item => Assert.Equal(UnderstandingOutcome.ProposedPending, item.Outcome));
        Assert.Null(Assert.Single(f.Owner.List(f.Scope, true)).Coverage); Assert.Empty(f.Owner.Query(f.Scope, "").Items);
        Assert.Equal("Completed", Assert.Single(dispatch.Reopen().Read()).State);
        Assert.Equal(1, provider.Calls); Assert.Equal(1, provider.Closed); Assert.Equal(0, provider.DiscoveryCalls);
        Assert.Equal(2048, Assert.Single(provider.Requests).MaxOutputTokens);
    }

    [Fact]
    public async Task Dispatch_logs_and_persistent_rows_omit_raw_ids_bodies_and_exception_values()
    {
        var events = new List<UnderstandingDiagnostic>();
        using var f = new DispatchFixture(diagnostics: d => { events.Add(d); throw new InvalidOperationException("PRIVATE_LOG_SINK"); });
        var primary = new InvalidOperationException("PRIVATE_EXCEPTION_BODY");
        Assert.Same(primary, await Record.ExceptionAsync(() => f.Run(f.Binding("PRIVATE_REQUEST_ID"), _ => throw primary)));
        var logs = JsonSerializer.Serialize(events); Assert.NotEmpty(events);
        foreach (var value in new[] { "PRIVATE_REQUEST_ID", "PRIVATE_EXCEPTION_BODY", "PRIVATE_LOG_SINK", "PRIVATE_TASK", "PRIVATE_OPERATION", "PRIVATE_INPUT_BODY" })
        {
            Assert.DoesNotContain(value, logs); Assert.DoesNotContain(value, Encoding.UTF8.GetString(File.ReadAllBytes(f.Path)));
        }
        Assert.All(events, d => { Assert.Matches("^[0-9A-F]{16}$", d.Correlation); Assert.StartsWith("dispatch_", d.Stage); });
    }

    [Theory]
    [InlineData("completed", false)]
    [InlineData("completed", true)]
    [InlineData("fault", false)]
    [InlineData("fault", true)]
    [InlineData("cancel", false)]
    [InlineData("cancel", true)]
    public async Task Dispatch_callback_reentry_finishes_without_refund_or_second_handoff(string completion, bool callbackFails)
    {
        var events = new System.Collections.Concurrent.ConcurrentQueue<UnderstandingDiagnostic>();
        using var f = new DispatchFixture(total: 1, diagnostics: events.Enqueue);
        var binding = f.Binding(); var permit = f.Authority.Issue(f.Ledger, binding); var calls = 0;
        var finish = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var reentered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationTokenRegistration registration = default; CancellationToken originalToken = default; Exception? primary = null;
        var task = permit.RunAsync(binding, _ => { }, token =>
        {
            calls++; originalToken = token;
            primary = completion switch
            {
                "fault" => new InvalidOperationException("PRIVATE_WORK_FAILURE"),
                "cancel" => new OperationCanceledException("PRIVATE_WORK_CANCELLATION", token),
                _ => null
            };
            registration = token.Register(() =>
            {
                entered.TrySetResult();
                f.Authority.Configure(f.Context, false);
                reentered.TrySetResult();
                if (callbackFails) throw new InvalidOperationException("PRIVATE_CALLBACK_FAILURE");
            });
            return finish.Task;
        }, _ => new(17, 5));
        var revocation = Task.Run(() => f.Authority.Configure(f.Context, false));
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(15));
            Assert.Equal("Reserved", Assert.Single(f.Reopen().Read()).State);
            var returned = await Task.WhenAny(reentered.Task, Task.Delay(TimeSpan.FromSeconds(5)));
            Assert.True(returned == reentered.Task, "REVIEW_DISPATCH_REENTRANT_REVOCATION_DID_NOT_RETURN");
            await revocation.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Throws<AgentProtocolException>(() => f.Authority.Issue(f.Ledger, f.Binding("still-revoked")));
            if (primary is null) finish.SetResult(7); else finish.SetException(primary);
            var error = await Record.ExceptionAsync(async () => await task.WaitAsync(TimeSpan.FromSeconds(5)));
            if (primary is not null) Assert.Same(primary, error);
            if (completion == "fault") Assert.True(task.IsFaulted);
            else
            {
                Assert.True(task.IsCanceled);
                Assert.Equal(originalToken, Assert.IsAssignableFrom<OperationCanceledException>(error).CancellationToken);
            }
            var row = Assert.Single(f.Reopen().Read());
            Assert.Equal(completion == "completed" ? "Completed" : "Unknown", row.State);
            Assert.Equal(completion == "completed" ? (int?)17 : null, row.InputTokens);
            Assert.Equal(completion == "completed" ? (int?)5 : null, row.OutputTokens);
            Assert.Equal(callbackFails, events.Any(d => d.Stage == "dispatch_cancel_callback_failed"));
            f.Authority.Configure(f.Context, true);
            await Assert.ThrowsAsync<AgentProtocolException>(() => f.Run(f.Binding("second"), _ => { calls++; return Task.FromResult(7); }));
            await Assert.ThrowsAsync<AgentProtocolException>(() => permit.RunAsync(binding, _ => { }, _ => { calls++; return Task.FromResult(7); }, _ => new(17, 5)));
            Assert.Equal(1, calls); Assert.Single(f.Reopen().Read());
        }
        finally
        {
            // Never let a regressed callback block the test's bounded failure path.
            // The dedicated test host owns any blocked background workers on failure.
            registration.Unregister();
        }
    }

    private static string DispatchHash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    [Fact]
    public async Task Dispatch_reentrant_revocation_runs_cancellation_callbacks_outside_the_admission_lock()
    {
        using var f = new DispatchFixture(); var binding = f.Binding();
        var observed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationTokenRegistration registration = default;
        var task = f.Run(binding, token =>
        {
            registration = token.Register(() =>
            {
                var query = Task.Run(() => Assert.Throws<AgentProtocolException>(() => f.Authority.Issue(f.Ledger, f.Binding("blocked"))));
                observed.TrySetResult(query.Wait(TimeSpan.FromSeconds(3)));
            });
            f.Authority.Configure(f.Context, false);
            return Task.FromResult(7);
        });
        try
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await task);
            Assert.True(await observed.Task.WaitAsync(TimeSpan.FromSeconds(15)));
            Assert.Single(f.Ledger.Read());
        }
        finally { registration.Dispose(); }
    }

    [Fact]
    public async Task Dispatch_concurrent_request_logs_join_distinct_rows_even_with_one_operation()
    {
        var events = new System.Collections.Concurrent.ConcurrentQueue<UnderstandingDiagnostic>();
        using var f = new DispatchFixture(diagnostics: events.Enqueue);
        var finish = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var first = f.Run(f.Binding("one"), _ => finish.Task); var second = f.Run(f.Binding("two"), _ => finish.Task);
        finish.SetResult(7); await Task.WhenAll(first, second);
        var rows = f.Ledger.Read(); Assert.Equal(2, rows.Count);
        Assert.Single(rows.Select(r => r.OperationHash).Distinct());
        Assert.Equal(2, events.Select(d => d.Correlation).Distinct().Count());
        foreach (var row in rows) Assert.Contains(events, d => d.Correlation == row.RequestHash[..16]);
    }

    [Fact]
    public async Task Dispatch_last_completion_diagnostic_cannot_deliver_after_revocation()
    {
        using var f = new DispatchFixture(); ReviewDispatchAuthorityProbe? authority = null;
        authority = new(d => { if (d.Stage == "dispatch_completed") authority!.Configure(f.Context, false); });
        authority.Configure(f.Context, true); var binding = f.Binding(); var permit = authority.Issue(f.Ledger, binding); var calls = 0;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => permit.RunAsync(binding, _ => { },
            _ => { calls++; return Task.FromResult(7); }, _ => new(17, 5)));
        Assert.Equal(1, calls); Assert.Equal("Completed", Assert.Single(f.Ledger.Read()).State);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Dispatch_wrapped_exchange_preserves_read_or_cancel_error_when_close_also_fails(bool cancel)
    {
        using var f = new Fixture(); f.Allow(); using var dispatch = new DispatchFixture();
        Exception primary = cancel ? new OperationCanceledException("PRIVATE_READ_CANCEL") : new InvalidOperationException("PRIVATE_READ_ERROR");
        var stream = new FaultingReviewStream(primary, new InvalidOperationException("PRIVATE_CLOSE_ERROR"));
        var provider = new ReviewProvider { Open = _ => stream }; Exception? observed = null;
        f.ProduceAsync = async (request, token) =>
        {
            var binding = dispatch.Binding() with { InputHash = ReviewModelExchangeProbe.RequestBinding(request) };
            try
            {
                return (await dispatch.Authority.Issue(dispatch.Ledger, binding).RunAsync(binding, request.EnsureCurrent,
                    ct => new ReviewModelExchangeProbe(provider).RunAsync("PRIVATE_OPERATION", request, ct), result => result.Usage, token)).Proposals;
            }
            catch (Exception error) { observed = error; throw; }
        };
        if (cancel) Assert.Same(primary, await Record.ExceptionAsync(async () => await f.Run()));
        else Assert.All((await f.Run()).Items, item => Assert.Equal(UnderstandingOutcome.Failed, item.Outcome));
        Assert.Same(primary, observed); Assert.Equal(1, provider.Calls); Assert.Equal(1, stream.Closed);
        Assert.Equal("Unknown", Assert.Single(dispatch.Ledger.Read()).State); Assert.Empty(f.Owner.List(f.Scope, true));
    }

    [Fact]
    public async Task Dispatch_revoking_inflight_exchange_cancels_stream_and_never_commits_pending()
    {
        using var f = new Fixture(); f.Allow(); using var dispatch = new DispatchFixture();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); var closed = 0;
        var provider = new ReviewProvider { Open = Delayed };
        f.ProduceAsync = async (request, token) =>
        {
            var binding = dispatch.Binding() with { InputHash = ReviewModelExchangeProbe.RequestBinding(request) };
            return (await dispatch.Authority.Issue(dispatch.Ledger, binding).RunAsync(binding, request.EnsureCurrent,
                ct => new ReviewModelExchangeProbe(provider).RunAsync("PRIVATE_OPERATION", request, ct), result => result.Usage, token)).Proposals;
        };
        var task = f.Run();
        try
        {
            await started.Task.WaitAsync(TimeSpan.FromSeconds(15));
            dispatch.Authority.Configure(dispatch.Context, false); f.ChangePolicy(p => p with { ExternalProcessing = false });
        }
        finally { finish.TrySetResult(); }
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await task);
        Assert.Equal(1, provider.Calls); Assert.Equal(1, closed); Assert.Empty(f.Owner.List(f.Scope, true));
        Assert.Equal("Unknown", Assert.Single(dispatch.Ledger.Read()).State);
        async IAsyncEnumerable<ChatStreamChunk> Delayed(ChatRequest request)
        {
            try { started.SetResult(); await finish.Task; foreach (var frame in ReviewFrames(request)) yield return frame; }
            finally { closed++; }
        }
    }

    private sealed class DispatchFixture : IDisposable
    {
        private readonly string _directory = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "fgopet-review-dispatch-" + Guid.NewGuid().ToString("N"));
        private readonly int _total;
        public string Path => System.IO.Path.Combine(_directory, "dispatch-probe.db");
        public ReviewDispatchContext Context { get; } = new(DispatchHash("scope"), DispatchHash("route"), 1);
        public ReviewDispatchLedgerProbe Ledger { get; }
        public ReviewDispatchAuthorityProbe Authority { get; }
        public DispatchFixture(int total = 3, int batch = 3, int trial = 2, Action<UnderstandingDiagnostic>? diagnostics = null)
        {
            _total = total; Ledger = new(Path, DispatchHash("PRIVATE_TASK"), total);
            Ledger.Register(DispatchHash("batch"), DispatchHash("trial"), batch, trial);
            Authority = new(diagnostics); Authority.Configure(Context, true);
        }
        public ReviewDispatchLedgerProbe Reopen(int? total = null) => new(Path, DispatchHash("PRIVATE_TASK"), total ?? _total);
        public ReviewDispatchBinding Binding(string request = "request", string batch = "batch", string trial = "trial")
            => new(DispatchHash(batch), DispatchHash(trial), DispatchHash(request), DispatchHash("PRIVATE_OPERATION"), DispatchHash("PRIVATE_INPUT_BODY"), Context);
        public Task<int> Run(ReviewDispatchBinding binding, Func<CancellationToken, Task<int>> start, bool knownUsage = true)
            => Authority.Issue(Ledger, binding).RunAsync(binding, _ => { }, start, _ => knownUsage ? new(17, 5) : null);
        public void Dispose() { if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true); }
    }
}
