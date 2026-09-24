using System.IO;
using System.Reflection;
using System.Text.Json;
using FgoPet.App.Dialogue;
using FgoPet.App.Services;
using FgoPet.Core.Dialogue;
using FgoPet.Core.Memory;
using FgoPet.Core.Packs;
using FgoPet.Dialogue.Settings;
using FgoPet.Infrastructure.Dialogue;
using FgoPet.Infrastructure.Packs;
using FgoPet.Infrastructure.Persistence;
using Xunit;

namespace FgoPet.App.Tests.Dialogue;

public sealed class TodoConversationPortTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"fgo-todo-port-{Guid.NewGuid():N}.db");
    private readonly RuntimeDatabase _database;
    private readonly SqliteConversationRepository _conversations;
    private readonly SqliteTodoRepository _todos;
    private readonly ITodoDraftWorkflow _drafts;
    private static readonly ContentContextKey Key = new("mash", "test", "1", "default", "1", "1");

    public TodoConversationPortTests()
    {
        _database = new(_path, pooling: false);
        new RuntimeDatabaseMigrator(_database).Migrate();
        _conversations = new(_database);
        _todos = new(_database);
        // Real Work confirmation and persistence; only the dialogue-facing reader is replaced.
        _drafts = new TodoProposalService(new TodoApplicationService(_todos, TimeProvider.System)).Drafts;
    }

    [Fact]
    public void Orchestrator_depends_on_contracts_and_retains_only_read_access_to_proposals()
    {
        var parameter = Assert.Single(Assert.Single(typeof(ConversationOrchestrator).GetConstructors())
            .GetParameters().Where(item => item.Name == "todoProposals"));
        Assert.Equal(typeof(ITodoConversationPort), parameter.ParameterType);
        var fields = typeof(ConversationOrchestrator).GetFields(BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.Equal(typeof(ITodoProposalReader), Assert.Single(fields.Where(field => field.Name == "_todoProposals")).FieldType);
        Assert.Equal(typeof(ITodoDraftWorkflow), Assert.Single(fields.Where(field => field.Name == "_todoDrafts")).FieldType);
        Assert.DoesNotContain(fields, field => field.FieldType == typeof(TodoProposalService));
        Assert.Contains(typeof(ITodoConversationPort), typeof(TodoProposalService).GetInterfaces());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Replacement_reader_preserves_proposals_and_commits_only_after_explicit_confirmation(bool tools)
    {
        var port = new ReaderPort(_drafts);
        var provider = new Provider(tools);
        var orchestrator = Create(port, provider);
        var updates = new List<ConversationUpdate>();
        orchestrator.Updated += updates.Add;

        var sent = await orchestrator.SendAsync("mash", "帮我整理资料", default, new("project-a"));
        Assert.Equal(ConversationSendStatus.Completed, sent.Status);
        Assert.Empty(_todos.List());
        var draft = _drafts.Get(sent.ConversationId, "mash");
        Assert.NotNull(draft);
        Assert.Equal(port.Proposals, draft!.Proposals);
        var update = Assert.Single(updates.Where(item => item.Type == ConversationUpdateType.AssistantCompleted));
        Assert.Equal(tools ? TodoToolCallOutcome.ProposalsReady : TodoToolCallOutcome.TextFallback, update.TodoOutcome);
        Assert.Equal(draft.DraftId, update.TodoDraftId);
        Assert.Equal(draft.Version, update.TodoDraftVersion);
        Assert.Contains(provider.Requests[0].Messages, message => message.Text.Contains("port-runtime-state", StringComparison.Ordinal));
        Assert.Equal(tools ? 1 : 0, port.ToolReads);
        Assert.Equal(tools ? 0 : 1, port.EnvelopeReads);

        var confirmed = await orchestrator.SendAsync("mash", "确认创建", default, new("project-a"));
        Assert.Equal(ConversationSendStatus.Completed, confirmed.Status);
        Assert.Single(provider.Requests); // Confirmation is local, not another model request.
        var todo = Assert.Single(_todos.List());
        Assert.Equal("整理资料", todo.Title);
        Assert.Equal("只整理，不执行", todo.Description);
        Assert.Equal(new[] { "查看笔记", "整理目录" }, todo.Steps.Select(step => step.Title));
        Assert.Null(_drafts.Get(sent.ConversationId, "mash"));
        var retry = _drafts.Confirm(sent.ConversationId, "mash", draft.DraftId, draft.Version,
            $"todo-confirm:{sent.ConversationId}:{draft.DraftId}:{draft.Version}");
        Assert.Equal(TodoDraftResultKind.AlreadyCommitted, retry.Kind);
        Assert.Equal(todo.Id, retry.Todo?.Id);
        Assert.Single(_todos.List());
    }

    [Theory]
    [InlineData("同意")]
    [InlineData("不要创建")]
    [InlineData("确认创建吗")]
    [InlineData("“确认创建”")]
    [InlineData("先修改再确认")]
    public async Task Vague_quoted_or_negated_confirmation_does_not_write_through_the_port(string text)
    {
        var port = new ReaderPort(_drafts);
        var provider = new Provider(false);
        var orchestrator = Create(port, provider);
        var sent = await orchestrator.SendAsync("mash", "整理资料", default);
        var draft = _drafts.Get(sent.ConversationId, "mash");
        Assert.NotNull(draft);
        port.OfferProposal = false;
        Assert.Equal(ConversationSendStatus.Completed, (await orchestrator.SendAsync("mash", text, default)).Status);
        Assert.Empty(_todos.List());
        Assert.Same(draft, _drafts.Get(sent.ConversationId, "mash"));
        Assert.Equal(2, provider.Requests.Count);
    }

    [Fact]
    public async Task Replacement_invalidates_the_old_version_and_preserves_the_current_fields()
    {
        var port = new ReaderPort(_drafts);
        var orchestrator = Create(port, new Provider(false));
        var sent = await orchestrator.SendAsync("mash", "整理资料", default);
        var old = _drafts.Get(sent.ConversationId, "mash")!;
        port.Proposals = [new("更新后的任务", "保留完整描述", stepTitles: ["更新后的步骤"])];
        Assert.Equal(ConversationSendStatus.Completed, (await orchestrator.SendAsync("mash", "修改标题和步骤", default)).Status);
        var current = _drafts.Get(sent.ConversationId, "mash")!;
        Assert.Equal(old.Version + 1, current.Version);
        Assert.Equal(TodoDraftResultKind.Stale, _drafts.Confirm(sent.ConversationId, "mash", old.DraftId, old.Version, "old").Kind);
        Assert.Empty(_todos.List());
        Assert.Equal(ConversationSendStatus.Completed, (await orchestrator.SendAsync("mash", "确认创建", default)).Status);
        var todo = Assert.Single(_todos.List());
        Assert.Equal("更新后的任务", todo.Title);
        Assert.Equal("保留完整描述", todo.Description);
        Assert.Equal("更新后的步骤", Assert.Single(todo.Steps).Title);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Cancel_or_new_conversation_removes_the_shared_draft_without_writing(bool newConversation)
    {
        var port = new ReaderPort(_drafts);
        var provider = new Provider(false);
        var orchestrator = Create(port, provider);
        var sent = await orchestrator.SendAsync("mash", "整理资料", default);
        var draft = _drafts.Get(sent.ConversationId, "mash")!;
        if (newConversation) orchestrator.StartNewConversation("mash");
        else Assert.Equal(ConversationSendStatus.Completed, (await orchestrator.SendAsync("mash", "取消草稿", default)).Status);
        Assert.Null(_drafts.Get(sent.ConversationId, "mash"));
        Assert.Empty(_todos.List());
        Assert.Single(provider.Requests);
        Assert.Equal(TodoDraftResultKind.Stale, _drafts.Confirm(sent.ConversationId, "mash", draft.DraftId, draft.Version, "late").Kind);
    }

    [Fact]
    public async Task Explicit_workflow_override_remains_the_one_used_for_creation_and_confirmation()
    {
        var port = new ReaderPort(_drafts);
        var overrideDrafts = new TodoProposalService(new TodoApplicationService(_todos, TimeProvider.System)).Drafts;
        var orchestrator = Create(port, new Provider(false), overrideDrafts);
        var sent = await orchestrator.SendAsync("mash", "整理资料", default);
        Assert.Null(port.Drafts.Get(sent.ConversationId, "mash"));
        Assert.NotNull(overrideDrafts.Get(sent.ConversationId, "mash"));
        Assert.Equal(ConversationSendStatus.Completed, (await orchestrator.SendAsync("mash", "确认创建", default)).Status);
        Assert.Single(_todos.List());
    }

    [Fact]
    public async Task Reader_rejection_does_not_replace_an_existing_draft()
    {
        var port = new ReaderPort(_drafts);
        var provider = new Provider(true);
        var orchestrator = Create(port, provider);
        var sent = await orchestrator.SendAsync("mash", "整理资料", default);
        var draft = _drafts.Get(sent.ConversationId, "mash");
        Assert.NotNull(draft);
        port.RejectTool = true;
        var updates = new List<ConversationUpdate>();
        orchestrator.Updated += updates.Add;
        Assert.Equal(ConversationSendStatus.Completed, (await orchestrator.SendAsync("mash", "继续修改", default)).Status);
        Assert.Same(draft, _drafts.Get(sent.ConversationId, "mash"));
        Assert.Empty(_todos.List());
        Assert.Contains(updates, update => update.TodoOutcome == TodoToolCallOutcome.InvalidToolCall);
    }

    [Fact]
    public void Production_reader_still_rejects_execution_fields_and_does_not_create_todos()
    {
        ITodoConversationPort port = new TodoProposalService(new TodoApplicationService(_todos, TimeProvider.System));
        Assert.Same(port.Drafts, port.Drafts);
        ITodoProposalReader reader = port;
        using var document = JsonDocument.Parse("""{"todos":[{"title":"整理资料","command":"run-something"}]}""");
        var result = reader.TryParseToolCall(document.RootElement);
        Assert.False(result.Success);
        Assert.Equal(TodoToolCallFailure.UnsupportedField, result.Failure);
        Assert.Null(result.Proposals);
        Assert.Single(reader.ParseEnvelope("""{"todos":[{"title":"安全提案"}]}""")!);
        Assert.Throws<FormatException>(() => reader.ParseEnvelope("""{"todos":[{"title":"整理资料","command":"run-something"}]}"""));
        Assert.Empty(_todos.List());
    }

    private ConversationOrchestrator Create(ITodoConversationPort port, Provider provider, ITodoDraftWorkflow? drafts = null) =>
        new(new Resolver(provider), new Content(), _conversations, new NoMemory(), new PromptComposer(), TimeProvider.System,
            settings: new Settings(provider.Tools), todoProposals: port, todoDrafts: drafts);

    private sealed class ReaderPort(ITodoDraftWorkflow drafts) : ITodoConversationPort
    {
        public ITodoDraftWorkflow Drafts { get; } = drafts;
        public IReadOnlyList<TodoProposal> Proposals { get; set; } = [new("整理资料", "只整理，不执行", stepTitles: ["查看笔记", "整理目录"])];
        public bool OfferProposal { get; set; } = true;
        public bool RejectTool { get; set; }
        public int EnvelopeReads { get; private set; }
        public int ToolReads { get; private set; }
        public string BuildRuntimeState(string userMessage) => "port-runtime-state";
        public IReadOnlyList<TodoProposal>? ParseEnvelope(string modelResponse)
        {
            EnvelopeReads++;
            return OfferProposal ? Proposals : null;
        }
        public ToolCallProposalResult TryParseToolCall(JsonElement arguments)
        {
            ToolReads++;
            return RejectTool ? ToolCallProposalResult.Fail(TodoToolCallFailure.NotPlanning) : ToolCallProposalResult.Ok(Proposals);
        }
    }

    private sealed class NoMemory : IMemoryRecall
    {
        public MemoryRecallSnapshot Query(MemoryScope scope, string query, int maxItems = 8, int maxChars = 6000) => new(0, []);
    }
    private sealed class Settings(bool tools) : IDialogueSettingsStore
    {
        private DialogueSettings _value = DialogueSettings.Defaults with
        {
            ModelConnection = new("test", "https://fixture.test", "model", toolsSupported: tools, contextWindowOverride: 32768),
        };
        public DialogueSettings Load() => _value;
        public void Save(DialogueSettings settings) => _value = settings;
    }
    private sealed class Resolver(Provider provider) : IChatProviderResolver
    {
        public IChatProvider Resolve() => provider;
    }
    private sealed class Content : IConversationContentResolver
    {
        public Task<ContentBinding> ResolveAsync(string servantId, CancellationToken cancellationToken) => Task.FromResult(
            new ContentBinding(Key, new PersonaBundle("mash", "test", "1", "1", "认真回应。", []), [], [], new string('a', 64), new string('b', 64)));
    }
    private sealed class Provider(bool tools) : IChatProvider
    {
        public bool Tools { get; } = tools;
        public string ProviderId => "test";
        public string ModelId => "model";
        public List<ChatRequest> Requests { get; } = [];
        public Task<IReadOnlyList<ProviderModel>> ListModelsAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
        public async IAsyncEnumerable<ChatStreamChunk> StreamAsync(ChatRequest request,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
        {
            Requests.Add(request);
            cancellationToken.ThrowIfCancellationRequested();
            await Task.Yield();
            if (Tools)
                yield return new("", IsComplete: true, FinishReason: "tool_calls", ToolCallDelta:
                    new(0, "call-1", TodoToolContracts.SubmitTodoProposalsToolName, """{"todos":[{"title":"整理资料"}]}"""));
            else yield return new("""{"text":"已整理，请确认。","emotion":"neutral"}""", IsComplete: true, FinishReason: "stop");
        }
    }

    public void Dispose()
    {
        foreach (var suffix in new[] { "", "-wal", "-shm" }) File.Delete(_path + suffix);
    }
}
