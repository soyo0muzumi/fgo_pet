using System.Text;
using System.Text.Json;
using FgoPet.Core.Dialogue;
using FgoPet.Extensibility;
using FgoPet.Understanding.Contracts;
using Microsoft.Data.Sqlite;

namespace FgoPet.Understanding;

public sealed partial class UnderstandingService
{
    /// <summary>Independent Stage A commit. Does not acknowledge Self coverage or advance the Self revision.</summary>
    public UnderstandingReceipt CommitRecurrenceClues(UnderstandingTicket ticket, IReadOnlyList<RecurrenceClueDraft> drafts,
        IReadOnlyList<ReviewOriginal> originals, IReadOnlyList<ExperienceSourceHead> heads,
        Func<bool>? inputCurrent = null, CancellationToken token = default)
    {
        lock (_gate)
        {
            token.ThrowIfCancellationRequested(); EnsureSchema();
            using var db = database.Open();
            var operation = "clue_" + HashId(ticket.Id);
            if (ReadReceipt(db, null, ticket.Scope, operation) is { } replay) return replay;
            var version = DataVersion(db);
            var denial = CheckSources(ticket.Scope, ticket.Sources, ticket.Id);
            if (denial is not null) throw new InvalidOperationException(denial);
            if (originals.Any(o => !ticket.Sources.Contains(o.Source) || o.Coverage.Source != o.Source ||
                !o.Coverage.Complete || ticket.Coverage?.Contains(o.Coverage) != true ||
                Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(o.Text))) != o.Source.Fingerprint))
                throw InvalidClue();
            using var tx = db.BeginTransaction(); var state = ReadState(db, tx, ticket.Scope);
            if (!state.Policy.Enabled || !state.Policy.ReadSources || !state.Policy.ExternalProcessing ||
                !state.Policy.SaveCandidates || state.Policy.Mode == UnderstandingMode.Off)
                throw new InvalidOperationException("UNDERSTANDING_NOT_AUTHORIZED");
            if (state.Revision != ticket.InputRevision || state.Generation != ticket.Generation ||
                version != DataVersion(db, tx) || inputCurrent?.Invoke() == false)
                throw new InvalidOperationException("UNDERSTANDING_STALE_INPUT");
            SaveClues(db, tx, ticket, drafts, originals, heads);
            var receipt = new UnderstandingReceipt(operation, drafts.Count == 0 ? [new("clues", UnderstandingOutcome.NoChange)] :
                Enumerable.Range(0, drafts.Count).Select(i => new UnderstandingItemReceipt(
                    "clue_" + HashId(ticket.Scope.Key + "|" + ticket.Id + "|" + i)[..32], UnderstandingOutcome.Applied)).ToArray());
            WriteReceipt(db, tx, ticket.Scope, receipt);
            token.ThrowIfCancellationRequested(); tx.Commit(); Log(ticket.Id, "recurrence_clues_committed");
            return receipt;
        }
    }
    /// <summary>Second-order host access only. Deliberately absent from IUnderstandingReader and all prompt/query paths.</summary>
    public IReadOnlyList<RecurrenceClue> RecurrenceClues(ConversationScope scope)
    {
        lock (_gate)
        {
            EnsureSchema(); using var db = database.Open();
            var state = ReadState(db, null, scope);
            if (!state.Policy.Enabled || !state.Policy.ReadSources || !state.Policy.SaveCandidates || state.Policy.Mode == UnderstandingMode.Off)
                return [];
            var now = _clock.GetUtcNow();
            Execute(db, null, "DELETE FROM understanding_recurrence_clues WHERE scope=$scope AND expires_at<=$now",
                ("$scope", scope.Key), ("$now", now.ToUnixTimeMilliseconds()));
            using var cmd = Command(db, null,
                "SELECT clue_json FROM understanding_recurrence_clues WHERE scope=$scope ORDER BY observed_at DESC,clue_id LIMIT 64", ("$scope", scope.Key));
            var clues = new List<RecurrenceClue>();
            using (var reader = cmd.ExecuteReader())
                while (reader.Read()) clues.Add(JsonSerializer.Deserialize<RecurrenceClue>(reader.GetString(0))!);
            return clues.Where(c => c.ObservedAt <= now && c.ExpiresAt > now && c.SourceRefs.All(h => sources.IsCurrent(h.Source))).ToArray();
        }
    }

    private void SaveClues(SqliteConnection db, SqliteTransaction tx, UnderstandingTicket ticket,
        IReadOnlyList<RecurrenceClueDraft> drafts, IReadOnlyList<ReviewOriginal> originals,
        IReadOnlyList<ExperienceSourceHead> heads)
    {
        if (drafts.Count > 2) throw InvalidClue();
        var now = _clock.GetUtcNow();
        for (var index = 0; index < drafts.Count; index++)
        {
            var draft = drafts[index];
            if (draft is null || string.IsNullOrWhiteSpace(draft.Theme) || draft.Theme.Length > 160 ||
                string.IsNullOrWhiteSpace(draft.Summary) || draft.Summary.Length > 600 ||
                draft.Expressions is not { Count: > 0 and <= 4 }) throw InvalidClue();
            foreach (var expression in draft.Expressions)
            {
                if (expression?.Source is null || expression.Source.Kind != ConversationSourceKind.Assistant ||
                    string.IsNullOrWhiteSpace(expression.Quote) || expression.Quote.Length > 12000 ||
                    !ticket.Sources.Contains(expression.Source) || !originals.Any(o => o.Source == expression.Source &&
                        o.Coverage.Source == o.Source && o.Coverage.Complete && ticket.Coverage?.Contains(o.Coverage) == true &&
                        o.Text.Contains(expression.Quote, StringComparison.Ordinal))) throw InvalidClue();
            }
            var conversations = draft.Expressions.Select(e => e.Source.Scope.ConversationId).Distinct().ToArray();
            if (conversations.Length != 1) throw InvalidClue();
            // Keep the original user context as well as the Assistant: later authorship checks must not see a decontextualized quote.
            var refs = heads.Where(h => conversations.Contains(h.Source.Scope.ConversationId)).Distinct().ToArray();
            var context = originals.Where(o => conversations.Contains(o.Source.Scope.ConversationId)).ToArray();
            if (refs.Length is < 1 or > 8 || draft.Expressions.Any(e => !refs.Any(h => h.Source == e.Source)) ||
                refs.Length != context.Length || context.Any(o => !o.Coverage.Complete) ||
                refs.Any(h => !ticket.Sources.Contains(h.Source) || h.Anchor.ConversationId != h.Source.Scope.ConversationId ||
                    h.Anchor.MessageId != h.Source.MessageId || h.Anchor.Sequence < 1 || h.Source.OccurredAtUtc > now ||
                    !originals.Any(o => o.Source == h.Source && o.Coverage.Complete && o.Coverage.TotalScalars == h.TotalScalars &&
                        o.Text.EnumerateRunes().Count() == h.TotalScalars)) || refs.Sum(h => h.TotalScalars) > 6000) throw InvalidClue();
            var clue = new RecurrenceClue("clue_" + HashId(ticket.Scope.Key + "|" + ticket.Id + "|" + index)[..32],
                draft.Theme, draft.Summary, refs, now, now.AddDays(14));
            Execute(db, tx, "INSERT OR IGNORE INTO understanding_recurrence_clues VALUES($scope,$id,$observed,$expires,$json)",
                ("$scope", ticket.Scope.Key), ("$id", clue.Id), ("$observed", now.ToUnixTimeMilliseconds()),
                ("$expires", clue.ExpiresAt.ToUnixTimeMilliseconds()), ("$json", JsonSerializer.Serialize(clue)));
        }
        // Hard capacity bound; retain newest hints, never grow an unbounded parallel memory store.
        Execute(db, tx, "DELETE FROM understanding_recurrence_clues WHERE scope=$scope AND (expires_at<=$now OR clue_id NOT IN " +
            "(SELECT clue_id FROM understanding_recurrence_clues WHERE scope=$scope ORDER BY observed_at DESC,clue_id LIMIT 64))",
            ("$scope", ticket.Scope.Key), ("$now", now.ToUnixTimeMilliseconds()));
    }
    private static ArgumentException InvalidClue() => new("UNDERSTANDING_INVALID_RECURRENCE_CLUE");
}
