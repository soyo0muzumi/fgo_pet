using FgoPet.Understanding;
using FgoPet.Understanding.Contracts;
using Xunit;

namespace FgoPet.Plugin.Understanding.Tests;

public sealed class DailyReviewLedgerTests
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), "fgo-daily-review-" + Guid.NewGuid().ToString("N"), "budget.db");
    private readonly Clock _clock = new();
    private static readonly string Route = new('A', 64);
    private static ReviewCallPlan Plan(string scope = "scope", string? id = null, string purpose = "review") =>
        new(ReviewBatch.Formal, "daily", scope, id ?? Guid.NewGuid().ToString("N"), Route, purpose, UnderstandingMode.Bounded, 1, new('B', 64), 1000);

    [Fact] public void Default_denies_and_authorization_is_exact_scope_and_route()
    {
        var ledger = new DailyReviewLedger(_path, _clock);
        Assert.Null(ledger.TryReserve(Plan()));
        ledger.Authorize("scope", Route, 12);
        Assert.Null(ledger.TryReserve(Plan("other")));
        Assert.Null(ledger.TryReserve(Plan() with { RouteHash = new('C',64) }));
        Assert.NotNull(ledger.TryReserve(Plan()));
        ledger.Revoke("scope");
        Assert.Null(ledger.TryReserve(Plan()));
    }

    [Fact] public void All_scopes_routes_and_restarts_share_daily_precharges_without_refunds()
    {
        var ledger = new DailyReviewLedger(_path, _clock);
        ledger.Authorize("scope", Route, 2); ledger.Authorize("other", Route, 2);
        var reservation = Assert.IsType<ReviewCallReservation>(ledger.TryReserve(Plan()));
        ledger.Complete(reservation.RequestHash, ReviewCallOutcome.Unknown);
        var restarted = new DailyReviewLedger(_path, _clock);
        Assert.NotNull(restarted.TryReserve(Plan("other", purpose: "classify")));
        restarted.Authorize("third", new('C',64), 2);
        Assert.Null(restarted.TryReserve(Plan("third") with { RouteHash = new('C',64) }));
        Assert.Equal(2, restarted.Status("scope", Route).Used);
    }

    [Fact] public void Utc_rollover_restores_allowance_but_clock_rollback_does_not()
    {
        var ledger = new DailyReviewLedger(_path, _clock); ledger.Authorize("scope", Route, 1);
        Assert.NotNull(ledger.TryReserve(Plan()));
        _clock.Now = _clock.Now.AddDays(1);
        Assert.NotNull(ledger.TryReserve(Plan()));
        _clock.Now = _clock.Now.AddDays(-1);
        Assert.Null(ledger.TryReserve(Plan()));
        Assert.Equal("UNDERSTANDING_CLOCK_ROLLBACK", ledger.Status("scope", Route).Code);
    }

    [Fact] public void Duplicate_and_invalid_requests_do_not_charge()
    {
        var ledger = new DailyReviewLedger(_path, _clock); ledger.Authorize("scope", Route, 12);
        var plan = Plan(); Assert.NotNull(ledger.TryReserve(plan)); Assert.Null(ledger.TryReserve(plan));
        Assert.Null(ledger.TryReserve(Plan() with { EstimatedInputTokens = 16001 }));
        Assert.Null(ledger.TryReserve(Plan(purpose: "reply")));
        Assert.Null(ledger.TryReserve(Plan() with { Mode = UnderstandingMode.Off }));
        Assert.Equal(1, ledger.Status("scope", Route).Used);
    }

    [Fact] public async Task Concurrent_instances_cannot_overspend()
    {
        new DailyReviewLedger(_path, _clock).Authorize("scope", Route, 3);
        var results = await Task.WhenAll(Enumerable.Range(0, 16).Select(_ => Task.Run(() => new DailyReviewLedger(_path, _clock).TryReserve(Plan()))));
        Assert.Equal(3, results.Count(r => r is not null));
    }

    [Fact] public void Corrupt_store_is_not_replaced()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!); File.WriteAllText(_path, "invalid-store");
        Assert.ThrowsAny<Exception>(() => new DailyReviewLedger(_path, _clock).Authorize("scope", Route, 12));
        Assert.Equal("invalid-store", File.ReadAllText(_path));
    }

    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now = new(2026, 10, 24, 12, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }
}
