using System.IO;
using System.Text;
using FgoPet.App.Memory;
using FgoPet.App.Providers;
using FgoPet.Core.Dialogue;
using FgoPet.Core.Memory;
using FgoPet.Core.Packs;
using FgoPet.Core.Portraits;
using FgoPet.Core.Settings;
using FgoPet.Infrastructure.Dialogue;
using FgoPet.Infrastructure.Memory;
using FgoPet.Infrastructure.Packs;
using FgoPet.Infrastructure.Persistence;
using FgoPet.Infrastructure.Providers;

namespace FgoPet.App.Dialogue;

public interface IChatProviderResolver
{
    IChatProvider Resolve();
}

public interface IConversationContentResolver
{
    Task<ContentBinding> ResolveAsync(string servantId, CancellationToken cancellationToken);
}

public sealed class ConfiguredChatProviderResolver : IChatProviderResolver
{
    private readonly IAppSettingsStore _settings;
    private readonly ChatProviderFactory _factory;

    public ConfiguredChatProviderResolver(IAppSettingsStore settings, ChatProviderFactory factory)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _factory = factory ?? throw new ArgumentNullException(nameof(factory));
    }

    public IChatProvider Resolve()
    {
        var settings = _settings.Load().ModelConnection;
        return settings is null
            ? throw new ProviderRequestException(ProviderFailureCategory.Configuration, "尚未配置模型连接。")
            : _factory.Create(settings);
    }
}

public sealed class InstalledContentBindingResolver : IConversationContentResolver
{
    private readonly IArtPackageRepository _repository;
    private readonly IAppSettingsStore _settings;

    public InstalledContentBindingResolver(IArtPackageRepository repository, IAppSettingsStore settings)
    {
        _repository = repository ?? throw new ArgumentNullException(nameof(repository));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
    }

    public async Task<ContentBinding> ResolveAsync(string servantId, CancellationToken cancellationToken)
    {
        var catalog = await _repository.ScanAsync(cancellationToken);
        var candidates = catalog.Packs
            .Where(pack => string.Equals(pack.ServantId, servantId, StringComparison.Ordinal))
            .ToArray();
        var selection = _settings.Load().Selection;
        var selected = candidates.FirstOrDefault(pack =>
                selection is not null
                && pack.PackageId == selection.PackageId
                && (selection.PackageVersion is null || pack.PackageVersion == selection.PackageVersion)
                && pack.Appearances.Any(appearance => appearance.AppearanceId == selection.AppearanceId))
            ?? candidates.OrderByDescending(pack => pack.Version).FirstOrDefault();
        if (selected is null)
        {
            throw new ProviderRequestException(ProviderFailureCategory.Configuration, "当前从者没有可用角色包。");
        }

        var appearanceId = selected.Appearances.FirstOrDefault(appearance =>
                selection is not null && appearance.AppearanceId == selection.AppearanceId)?.AppearanceId
            ?? selected.Appearances.First().AppearanceId;
        return ContentBindingResolver.Resolve(selected.PackRoot, servantId, appearanceId);
    }
}

public sealed class ConversationOrchestrator
{
    private readonly IChatProviderResolver _providerResolver;
    private readonly IConversationContentResolver _contentResolver;
    private readonly SqliteConversationRepository _conversations;
    private readonly SqliteMemoryRepository _memories;
    private readonly PromptComposer _composer;
    private readonly TimeProvider _time;
    private readonly IAppSettingsStore? _settings;
    private readonly ConversationSummaryService? _summaries;
    private readonly TodoProposalService? _todoProposals;
    private readonly object _gate = new();
    private readonly Dictionary<string, string> _conversationIds = new(StringComparer.Ordinal);
    private CancellationTokenSource? _activeCancellation;

    public ConversationOrchestrator(
        IChatProviderResolver providerResolver,
        IConversationContentResolver contentResolver,
        SqliteConversationRepository conversations,
        SqliteMemoryRepository memories,
        PromptComposer composer,
        TimeProvider time,
        IAppSettingsStore? settings = null,
        ConversationSummaryService? summaries = null,
        TodoProposalService? todoProposals = null)
    {
        _providerResolver = providerResolver ?? throw new ArgumentNullException(nameof(providerResolver));
        _contentResolver = contentResolver ?? throw new ArgumentNullException(nameof(contentResolver));
        _conversations = conversations ?? throw new ArgumentNullException(nameof(conversations));
        _memories = memories ?? throw new ArgumentNullException(nameof(memories));
        _composer = composer ?? throw new ArgumentNullException(nameof(composer));
        _time = time ?? throw new ArgumentNullException(nameof(time));
        _settings = settings;
        _summaries = summaries;
        _todoProposals = todoProposals;
    }

    public event Action<ConversationUpdate>? Updated;

    public async Task<ConversationSendResult> SendAsync(
        string servantId,
        string userText,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(servantId);
        ArgumentException.ThrowIfNullOrWhiteSpace(userText);
        using var requestCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        lock (_gate)
        {
            if (_activeCancellation is not null)
            {
                return new ConversationSendResult(ConversationSendStatus.Failed, string.Empty, SafeError: "当前已有对话请求正在进行。 ");
            }

            _activeCancellation = requestCancellation;
        }

        var conversationId = string.Empty;
        ContentContextKey? contentContext = null;
        try
        {
            var binding = await _contentResolver.ResolveAsync(servantId, requestCancellation.Token);
            if (binding.Context.ServantId != servantId)
            {
                throw new InvalidDataException("Content binding servant_id does not match the request.");
            }

            contentContext = binding.Context;
            conversationId = GetOrCreateConversation(servantId, contentContext);
            var allMessages = _conversations.LoadMessages(conversationId, servantId).ToArray();
            var existing = allMessages
                .Where(message => message.Status == ChatMessageStatus.Completed)
                .ToArray();
            var now = _time.GetUtcNow();
            var userMessage = new ChatMessage(
                "message-" + Guid.NewGuid().ToString("N"),
                conversationId,
                servantId,
                ChatMessageRole.User,
                userText,
                ChatMessageStatus.Completed,
                now,
                contentContext,
                allMessages.Length + 1);
            _conversations.Append(userMessage);
            Publish(new ConversationUpdate(
                ConversationUpdateType.UserMessagePersisted,
                conversationId,
                userMessage.MessageId,
                userMessage.Text,
                ServantId: servantId));

            var persona = binding.Persona ?? FallbackPersona(binding.Context);
            var prompt = _composer.Compose(new PromptContext(
                binding.Context,
                persona,
                binding.Knowledge,
                IsMemoryEnabled() ? _memories.ListEnabledMemories(servantId) : Array.Empty<StoredMemory>(),
                _todoProposals?.BuildRuntimeState(userText) ?? string.Empty,
                existing.Select(message => new PromptMessage(message.Role, message.Text)).ToArray(),
                userText));
            var request = new ChatRequest(servantId, conversationId, prompt.Messages, binding.Context);
            var provider = _providerResolver.Resolve();
            var responseText = new StringBuilder();
            var assistantId = "message-" + Guid.NewGuid().ToString("N");
            await foreach (var chunk in provider.StreamAsync(request, requestCancellation.Token))
            {
                requestCancellation.Token.ThrowIfCancellationRequested();
                if (!string.IsNullOrEmpty(chunk.TextDelta))
                {
                    responseText.Append(chunk.TextDelta);
                }

                if (chunk.IsComplete)
                {
                    break;
                }
            }

            requestCancellation.Token.ThrowIfCancellationRequested();
            IReadOnlyList<TodoProposal>? todoProposals = null;
            if (_todoProposals is not null)
            {
                try
                {
                    todoProposals = _todoProposals.ParseEnvelope(responseText.ToString());
                }
                catch (FormatException)
                {
                    // Structured proposals are optional and must never fail an ordinary reply.
                }
            }

            var output = StructuredOutputValidator.Validate(
                responseText.ToString(),
                ExpressionSemanticKeys.Core.ToHashSet(StringComparer.Ordinal));
            Publish(new ConversationUpdate(
                ConversationUpdateType.AssistantDelta,
                conversationId,
                assistantId,
                output.Text,
                ServantId: servantId));
            var assistant = new ChatMessage(
                assistantId,
                conversationId,
                servantId,
                ChatMessageRole.Assistant,
                output.Text,
                ChatMessageStatus.Completed,
                _time.GetUtcNow(),
                contentContext,
                _conversations.LoadMessages(conversationId, servantId).Count + 1);
            _conversations.Append(assistant);
            if (output.MemoryCandidate is not null && IsMemoryEnabled())
            {
                _memories.AddCandidate(new MemoryCandidate(
                    "candidate-" + Guid.NewGuid().ToString("N"),
                    servantId,
                    conversationId,
                    output.MemoryCandidate.Text,
                    _time.GetUtcNow(),
                    assistant.MessageId,
                    contentContext.AppearanceId));
            }

            if (_summaries is not null && IsMemoryEnabled())
            {
                try
                {
                    await _summaries.MaybeSummarizeAsync(conversationId, servantId, requestCancellation.Token);
                }
                catch (Exception)
                {
                    // Summary maintenance must not turn a completed dialogue into a failed turn.
                }
            }

            Publish(new ConversationUpdate(
                ConversationUpdateType.AssistantCompleted,
                conversationId,
                assistant.MessageId,
                output.Text,
                ServantId: servantId,
                StructuredResponse: todoProposals is { Count: > 0 } ? responseText.ToString() : null));
            return new ConversationSendResult(ConversationSendStatus.Completed, conversationId, assistant.MessageId);
        }
        catch (OperationCanceledException) when (requestCancellation.IsCancellationRequested)
        {
            Publish(new ConversationUpdate(ConversationUpdateType.Cancelled, conversationId, ServantId: servantId));
            return new ConversationSendResult(ConversationSendStatus.Cancelled, conversationId);
        }
        catch (ProviderRequestException error)
        {
            var safeError = error.Message;
            TryAppendFailedMessage(conversationId, servantId, contentContext);
            Publish(new ConversationUpdate(ConversationUpdateType.Failed, conversationId, SafeError: safeError, ServantId: servantId));
            var status = error.Category == ProviderFailureCategory.Configuration
                ? ConversationSendStatus.ConfigurationRequired
                : ConversationSendStatus.Failed;
            return new ConversationSendResult(status, conversationId, SafeError: safeError);
        }
        catch (FormatException)
        {
            const string safeError = "模型返回格式无法识别。";
            TryAppendFailedMessage(conversationId, servantId, contentContext);
            Publish(new ConversationUpdate(ConversationUpdateType.Failed, conversationId, SafeError: safeError, ServantId: servantId));
            return new ConversationSendResult(ConversationSendStatus.Failed, conversationId, SafeError: safeError);
        }
        catch (Exception)
        {
            const string safeError = "对话服务暂时不可用。";
            TryAppendFailedMessage(conversationId, servantId, contentContext);
            Publish(new ConversationUpdate(ConversationUpdateType.Failed, conversationId, SafeError: safeError, ServantId: servantId));
            return new ConversationSendResult(ConversationSendStatus.Failed, conversationId, SafeError: safeError);
        }
        finally
        {
            lock (_gate)
            {
                if (ReferenceEquals(_activeCancellation, requestCancellation))
                {
                    _activeCancellation = null;
                }
            }
        }
    }

    public void CancelCurrent()
    {
        lock (_gate)
        {
            _activeCancellation?.Cancel();
        }
    }

    public void StartNewConversation(string servantId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(servantId);
        lock (_gate)
        {
            _conversationIds.Remove(servantId);
        }
    }

    private string GetOrCreateConversation(string servantId, ContentContextKey context)
    {
        lock (_gate)
        {
            if (_conversationIds.TryGetValue(servantId, out var conversationId))
            {
                return conversationId;
            }

            conversationId = "conversation-" + Guid.NewGuid().ToString("N");
            _conversations.CreateConversation(conversationId, servantId, context, _time.GetUtcNow());
            _conversationIds[servantId] = conversationId;
            return conversationId;
        }
    }

    private void TryAppendFailedMessage(string conversationId, string servantId, ContentContextKey? context)
    {
        if (string.IsNullOrWhiteSpace(conversationId) || context is null)
        {
            return;
        }

        try
        {
            _conversations.Append(new ChatMessage(
                "message-" + Guid.NewGuid().ToString("N"),
                conversationId,
                servantId,
                ChatMessageRole.Assistant,
                string.Empty,
                ChatMessageStatus.Failed,
                _time.GetUtcNow(),
                context,
                _conversations.LoadMessages(conversationId, servantId).Count + 1));
        }
        catch (Exception)
        {
            // A secondary persistence failure must not replace the safe provider error.
        }
    }

    private void Publish(ConversationUpdate update)
    {
        try
        {
            Updated?.Invoke(update);
        }
        catch (Exception)
        {
            // UI observers are not allowed to break persistence or cancellation.
        }
    }

    private bool IsMemoryEnabled() => _settings?.Load().MemoryEnabled ?? true;

    private static PersonaBundle FallbackPersona(ContentContextKey context) =>
        new(context.ServantId, context.PackageId, context.PackageVersion, context.PersonaVersion, "保持自然、简洁地回应用户。", []);
}
