using FgoPet.Core.Bond;
using FgoPet.Core.Events;
using FgoPet.Core.Focus;
using FgoPet.Core.Timeline;
using FgoPet.Infrastructure.Bond;
using FgoPet.Infrastructure.Events;
using FgoPet.Infrastructure.Persistence;
using FgoPet.Infrastructure.Timeline;
using Microsoft.Data.Sqlite;

namespace FgoPet.Infrastructure.Focus;

/// <summary>
/// The consistency boundary: one completion commits the session snapshot, runtime
/// event, timeline projection, bond ledger, and bond total in a single transaction.
/// Replaying the same event is a no-op (idempotent).
/// </summary>
public sealed class SqliteFocusCompletionUnit
{
    public const string BondLevelUpEventPrefix = "bond-up-";

    private readonly RuntimeDatabase _database;
    private readonly SqliteEventStore _events;
    private readonly SqliteTimelineRepository _timeline;
    private readonly SqliteBondRepository _bonds;
    private readonly IBondProgressionPolicy _policy;

    public SqliteFocusCompletionUnit(
        RuntimeDatabase database,
        SqliteEventStore events,
        SqliteTimelineRepository timeline,
        SqliteBondRepository bonds,
        IBondProgressionPolicy policy)
    {
        _database = database;
        _events = events;
        _timeline = timeline;
        _bonds = bonds;
        _policy = policy;
    }

    public void CompleteFocus(FocusSession nextSession, RuntimeEvent completedEvent)
    {
        using var connection = _database.Open();
        using var transaction = connection.BeginTransaction();
        try
        {
            var inserted = _events.TryInsert(completedEvent, connection, transaction);
            if (!inserted)
            {
                // Replay of an already-committed completion: keep the stored snapshot
                // consistent without double-counting anything else.
                SqliteFocusRepository.DemoteCurrent(connection, transaction, nextSession.SessionId);
                SqliteFocusRepository.UpsertSnapshot(connection, transaction, nextSession);
                transaction.Commit();
                return;
            }

            SqliteFocusRepository.DemoteCurrent(connection, transaction, nextSession.SessionId);
            SqliteFocusRepository.UpsertSnapshot(connection, transaction, nextSession);
            _timeline.Insert(Projection(completedEvent, bondLevel: null), connection, transaction);
            _bonds.AppendLedger(
                $"ledger-{completedEvent.EventId}",
                completedEvent.EventId,
                completedEvent.ServantId,
                completedEvent.EffectiveSeconds,
                completedEvent.OccurredAtUtc,
                connection,
                transaction);

            var existing = _bonds.GetBond(completedEvent.ServantId);
            var priorSeconds = existing?.LifetimeFocusSeconds ?? 0;
            var achievedLevel = existing?.AchievedLevel ?? 1;
            var totalSeconds = priorSeconds + completedEvent.EffectiveSeconds;
            var progress = _policy.Evaluate(totalSeconds, achievedLevel);
            _bonds.Upsert(
                new(completedEvent.ServantId, completedEvent.EffectiveSeconds, progress.Level, _policy.Version, completedEvent.OccurredAtUtc),
                connection,
                transaction);

            if (progress.Level > achievedLevel)
            {
                var bondEvent = new RuntimeEvent(
                    $"{BondLevelUpEventPrefix}{completedEvent.EventId}",
                    completedEvent.SessionId,
                    RuntimeEventType.BondLevelUp,
                    completedEvent.OccurredAtUtc,
                    completedEvent.CycleNumber,
                    completedEvent.Phase,
                    completedEvent.ServantId,
                    ElapsedSeconds: 0,
                    EffectiveSeconds: 0,
                    Priority: 1,
                    PayloadJson: $"{{\"level\":{progress.Level}}}");
                _events.TryInsert(bondEvent, connection, transaction);
                _timeline.Insert(Projection(bondEvent, bondLevel: progress.Level), connection, transaction);
            }

            transaction.Commit();
        }
        catch
        {
            transaction.Rollback();
            throw;
        }
    }

    private static TimelineEntry Projection(RuntimeEvent source, int? bondLevel) => new(
        $"entry-{source.EventId}",
        source.EventId,
        source.OccurredAtUtc,
        source.Type,
        source.ServantId,
        source.ElapsedSeconds,
        source.EffectiveSeconds,
        bondLevel);
}
