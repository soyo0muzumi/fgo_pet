using FgoPet.Core.Memory;
using FgoPet.Core.Settings;
using FgoPet.Infrastructure.Dialogue;

namespace FgoPet.App.Memory;

/// <summary>
/// Creates a bounded local summary when explicitly invoked after a turn. It does
/// not call a model or scan conversations in the background; the current
/// implementation keeps the summary deterministic until a later summarizer is
/// introduced behind this boundary.
/// </summary>
public sealed class ConversationSummaryService
{
    private const int RecentMessageWindow = 6;
    private readonly SqliteConversationRepository _conversations;
    private readonly IAppSettingsStore _settings;
    private readonly TimeProvider _clock;
    private readonly int _threshold;

    public ConversationSummaryService(
        SqliteConversationRepository conversations,
        IAppSettingsStore settings,
        TimeProvider clock,
        int threshold = 12)
    {
        _conversations = conversations ?? throw new ArgumentNullException(nameof(conversations));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        if (threshold < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(threshold));
        }

        _threshold = threshold;
    }

    public Task<ConversationSummary?> MaybeSummarizeAsync(
        string conversationId,
        string servantId,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!_settings.Load().MemoryEnabled)
        {
            return Task.FromResult<ConversationSummary?>(null);
        }

        var messages = _conversations.LoadMessages(conversationId, servantId)
            .Where(message => message.Status == FgoPet.Core.Dialogue.ChatMessageStatus.Completed
                && message.Role is FgoPet.Core.Dialogue.ChatMessageRole.User or FgoPet.Core.Dialogue.ChatMessageRole.Assistant)
            .OrderBy(message => message.Sequence)
            .ToList();
        if (messages.Count < _threshold)
        {
            return Task.FromResult<ConversationSummary?>(null);
        }

        var coveredCount = Math.Max(1, messages.Count - RecentMessageWindow);
        var covered = messages.Take(coveredCount).ToArray();
        var summaryText = string.Join(
            Environment.NewLine,
            covered.Select(message => $"{message.Role}: {message.Text}"));
        summaryText = summaryText.Length <= 6_000 ? summaryText : summaryText[..6_000];
        var now = _clock.GetUtcNow();
        var current = _conversations.LoadSummary(conversationId, servantId);
        var summary = new ConversationSummary(
            current?.SummaryId ?? conversationId,
            conversationId,
            servantId,
            summaryText,
            covered[^1].Sequence,
            covered[^1].MessageId,
            covered[^1].ContentContext,
            current?.CreatedAtUtc ?? now,
            now);
        _conversations.SaveSummary(summary);
        return Task.FromResult<ConversationSummary?>(summary);
    }
}
