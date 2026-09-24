using System.IO;
using System.Reflection;
using FgoPet.App.Archives;
using FgoPet.App.Dialogue;
using FgoPet.App.Services;
using FgoPet.App.ViewModels;
using FgoPet.Core.Archives;
using FgoPet.Core.Dialogue;
using FgoPet.Core.Memory;
using FgoPet.Core.Todo;
using FgoPet.Infrastructure.Dialogue;
using FgoPet.Infrastructure.Packs;
using FgoPet.Infrastructure.Persistence;
using Xunit;

namespace FgoPet.App.Tests.Dialogue;

public sealed class WorkCardBoundaryTests
{
    private static readonly DateTimeOffset Now = new(2026, 1, 3, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Desktop_consumers_depend_on_narrow_contracts_not_Work_service_classes()
    {
        CheckDependency(typeof(ConversationViewModel), "todoProposals", "_todoProposals", typeof(ILegacyTodoProposalPort));
        CheckDependency(typeof(ConversationViewModel), "archiveDrafts", "_archiveDrafts", typeof(IArchiveDraftConfirmation));
        CheckDependency(typeof(TodoProposalViewModel), "service", "_service", typeof(ILegacyTodoProposalConfirmation));
        CheckDependency(typeof(ArchiveDraftViewModel), "service", "_service", typeof(IArchiveDraftConfirmation));
        Assert.Contains(typeof(ILegacyTodoProposalPort), typeof(TodoProposalService).GetInterfaces());
        Assert.Contains(typeof(IArchiveDraftConfirmation), typeof(ArchiveDraftService).GetInterfaces());
        var parameters = Assert.Single(typeof(ConversationOrchestrator).GetConstructors()).GetParameters();
        Assert.DoesNotContain(parameters, parameter =>
            parameter.ParameterType == typeof(ILegacyTodoProposalPort) || parameter.ParameterType == typeof(IArchiveDraftConfirmation));
    }

    [Fact]
    public void Legacy_card_only_confirms_on_request_and_preserves_edits_steps_and_success_identity()
    {
        var port = new LegacyPort();
        var original = new TodoProposal("Original", "Original note", stepTitles: ["First", "Second"]);
        var card = new TodoProposalViewModel(original, port)
        {
            Title = "Edited", Description = "Edited note", Priority = TodoPriority.High,
            DueAt = Now.AddDays(2), IsExpanded = true,
        };
        var closed = 0;
        card.Closed += sender =>
        {
            Assert.Same(card, sender);
            Assert.True(card.IsAdded);
            Assert.False(card.IsExpanded);
            Assert.Empty(card.ErrorText);
            closed++;
        };
        Assert.Empty(port.Confirmed);
        var todo = card.Confirm();
        var sent = Assert.Single(port.Confirmed);
        Assert.Equal("Edited", sent.Title);
        Assert.Equal("Edited note", sent.Description);
        Assert.Equal(TodoPriority.High, sent.Priority);
        Assert.Equal(Now.AddDays(2), sent.DueAt);
        Assert.Equal<string>(["First", "Second"], sent.StepTitles);
        Assert.Equal("Original", original.Title);
        Assert.Equal("Original note", original.Description);
        Assert.Same(todo, card.Confirm());
        card.Remove();
        Assert.False(card.IsRemoved);
        Assert.Equal(todo.Id, card.CreatedTodoId);
        Assert.Single(port.Confirmed);
        Assert.Equal(1, closed);
    }

    [Fact]
    public void Removed_legacy_card_never_reaches_the_confirmation_port()
    {
        var port = new LegacyPort();
        var card = new TodoProposalViewModel(new("Discard"), port);
        var closed = 0;
        card.Closed += _ => closed++;
        card.Remove();
        card.Remove();
        Assert.Throws<InvalidOperationException>(() => card.Confirm());
        Assert.True(card.IsRemoved);
        Assert.False(card.IsAdded);
        Assert.Null(card.CreatedTodoId);
        Assert.Empty(port.Confirmed);
        Assert.Equal(1, closed);
    }

    [Fact]
    public void Legacy_port_failure_preserves_input_and_does_not_report_added_before_retry()
    {
        var error = new IOException("fixture-confirmation-failure");
        var port = new LegacyPort { Failure = error };
        var card = new TodoProposalViewModel(new("Original", stepTitles: ["Keep step"]), port)
        { Title = "Retry title", Description = "Retry note", IsExpanded = true };
        var closed = 0;
        card.Closed += _ => closed++;
        Assert.Same(error, Assert.Throws<IOException>(() => card.Confirm()));
        Assert.False(card.IsAdded);
        Assert.Null(card.CreatedTodoId);
        Assert.True(card.IsExpanded);
        Assert.Equal("Retry title", card.Title);
        Assert.Equal("Retry note", card.Description);
        Assert.Contains("添加失败", card.ErrorText);
        Assert.Equal(0, closed);
        port.Failure = null;
        var result = card.Confirm();
        Assert.Equal(result.Id, card.CreatedTodoId);
        Assert.Empty(card.ErrorText);
        Assert.Equal(1, closed);
        Assert.Equal(2, port.Confirmed.Count);
        Assert.All(port.Confirmed, proposal => Assert.Equal<string>(["Keep step"], proposal.StepTitles));
    }

    [Fact]
    public void Archive_card_displays_without_writing_and_forwards_only_edited_title_and_summary()
    {
        var original = Draft();
        var port = new ArchivePort();
        var card = new ArchiveDraftViewModel(original, port) { Title = "Edited title", Summary = "Edited summary" };
        Assert.Equal(2, card.CoveredTodoCount);
        Assert.Empty(port.Confirmed);
        card.Confirm();
        Assert.Equal(original with { Title = "Edited title", Summary = "Edited summary" }, Assert.Single(port.Confirmed));
        Assert.Equal("Original title", original.Title);
        Assert.Equal("Original summary", original.Summary);
    }

    [Fact]
    public void Archive_failure_propagates_and_retains_edits_and_original_identity_for_retry()
    {
        var error = new IOException("fixture-archive-failure");
        var port = new ArchivePort { Failure = error };
        var original = Draft();
        var card = new ArchiveDraftViewModel(original, port) { Title = "Retry", Summary = "Keep summary" };
        Assert.Same(error, Assert.Throws<IOException>(card.Confirm));
        Assert.Same(original, card.Draft);
        Assert.Equal("Retry", card.Title);
        Assert.Equal("Keep summary", card.Summary);
        port.Failure = null;
        card.Confirm();
        Assert.Equal(2, port.Confirmed.Count);
        Assert.All(port.Confirmed, draft => Assert.Equal(original with { Title = "Retry", Summary = "Keep summary" }, draft));
    }

    [Fact]
    public void Conversation_loads_interface_only_cards_without_implicitly_confirming_them()
    {
        var legacy = new LegacyPort();
        var archive = new ArchivePort();
        using var fixture = new ConversationFixture(legacy, archive);
        var model = fixture.Model;
        Assert.True(model.TryLoadTodoProposals("synthetic fixture response"));
        var card = Assert.Single(model.TodoProposals);
        model.ShowArchiveDraft(Draft());
        var archiveCard = Assert.Single(model.ArchiveDrafts);
        Assert.Empty(legacy.Confirmed);
        Assert.Empty(archive.Confirmed);
        Assert.Equal("synthetic fixture response", Assert.Single(legacy.Parsed));
        card.Title = "Edited from conversation";
        card.Confirm();
        Assert.Equal("Edited from conversation", Assert.Single(legacy.Confirmed).Title);
        Assert.Same(card, Assert.Single(model.TodoProposals)); // Added cards retain their navigation target.
        archiveCard.Confirm();
        Assert.Single(archive.Confirmed);
    }

    [Fact]
    public void Invalid_legacy_parse_retains_existing_cards_and_remove_does_not_write()
    {
        var port = new LegacyPort();
        using var fixture = new ConversationFixture(port, null);
        var model = fixture.Model;
        Assert.True(model.TryLoadTodoProposals("first"));
        var original = Assert.Single(model.TodoProposals);
        port.ParseFailure = new FormatException("fixture-invalid-envelope");
        Assert.False(model.TryLoadTodoProposals("invalid"));
        Assert.Same(original, Assert.Single(model.TodoProposals));
        original.Remove();
        Assert.Empty(model.TodoProposals);
        Assert.Empty(port.Confirmed);
    }

    [Fact]
    public void Conversation_replacement_and_role_switch_preserve_the_nonwriting_card_lifecycle()
    {
        var legacy = new LegacyPort();
        var archive = new ArchivePort();
        using var fixture = new ConversationFixture(legacy, archive);
        var model = fixture.Model;
        model.SetActiveServant("mash");
        Assert.True(model.TryLoadTodoProposals("first"));
        var old = Assert.Single(model.TodoProposals);
        legacy.Proposals = [new("Replacement")];
        Assert.True(model.TryLoadTodoProposals("second"));
        var replacement = Assert.Single(model.TodoProposals);
        Assert.NotSame(old, replacement);
        old.Remove();
        Assert.Same(replacement, Assert.Single(model.TodoProposals));
        model.ShowArchiveDraft(Draft());
        model.SetActiveServant("other");
        Assert.Empty(model.TodoProposals);
        Assert.Empty(model.ArchiveDrafts);
        Assert.Empty(legacy.Confirmed);
        Assert.Empty(archive.Confirmed);
    }

    [Fact]
    public void Missing_optional_ports_keep_legacy_card_entry_points_inert()
    {
        using var fixture = new ConversationFixture(null, null);
        Assert.False(fixture.Model.TryLoadTodoProposals("fixture"));
        fixture.Model.ShowArchiveDraft(Draft());
        Assert.Empty(fixture.Model.TodoProposals);
        Assert.Empty(fixture.Model.ArchiveDrafts);
    }

    [Fact]
    public void Production_legacy_port_keeps_parser_rejection_and_confirmation_only_writes()
    {
        var todos = new TodoRepository();
        ILegacyTodoProposalPort port = new TodoProposalService(new TodoApplicationService(todos, TimeProvider.System));
        var proposal = Assert.Single(port.Parse("""{"title":"Review","steps":[{"title":"Keep notes"}]}"""));
        Assert.Single(port.Parse("""[{"title":"Array input"}]"""));
        Assert.Throws<FormatException>(() => port.Parse("""{"title":"Unsafe","command":"fixture-command"}"""));
        Assert.Empty(todos.Items);
        var card = new TodoProposalViewModel(proposal, port);
        var saved = card.Confirm();
        Assert.Same(saved, card.Confirm());
        Assert.Equal("Review", Assert.Single(todos.Items).Title);
        Assert.Equal("Keep notes", Assert.Single(saved.Steps).Title);
        Assert.Equal(1, todos.Writes);
    }

    [Fact]
    public void Production_archive_confirmation_keeps_identity_and_cleans_only_covered_completed_todos()
    {
        var todos = new TodoRepository();
        var completed = new TodoItem("covered", "Finished", "Note", TodoPriority.Normal, null,
            Now, Now, TodoStatus.Completed, Now);
        var unrelated = new TodoItem("unrelated", "Keep", null, TodoPriority.Normal, null, Now, Now);
        todos.Save(completed);
        todos.Save(unrelated);
        var archives = new ArchiveRepository(todos);
        var service = new ArchiveDraftService(todos, archives, TimeProvider.System);
        var draft = service.CreateDraft("fixture", [completed], "Summary");
        IArchiveDraftConfirmation port = service;
        var card = new ArchiveDraftViewModel(draft, port) { Title = "Edited archive", Summary = "Edited summary" };
        Assert.Empty(archives.Items);
        Assert.Equal(2, todos.Items.Count);
        card.Confirm();
        var saved = Assert.Single(archives.Items);
        Assert.Equal(draft.ArchiveId, saved.ArchiveId);
        Assert.Equal("Edited archive", saved.Title);
        Assert.Equal("Edited summary", saved.Summary);
        Assert.Equal<string>(["covered"], saved.CoveredTodoKeys);
        Assert.Equal("unrelated", Assert.Single(todos.Items).Id);
    }

    private static void CheckDependency(Type consumer, string parameter, string field, Type contract)
    {
        var constructor = Assert.Single(consumer.GetConstructors());
        Assert.Equal(contract, Assert.Single(constructor.GetParameters().Where(item => item.Name == parameter)).ParameterType);
        var fields = consumer.GetFields(BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.Equal(contract, Assert.Single(fields.Where(item => item.Name == field)).FieldType);
        Assert.DoesNotContain(fields, item => item.FieldType == typeof(TodoProposalService) || item.FieldType == typeof(ArchiveDraftService));
    }

    private static ArchiveDraft Draft() => new("archive-fixture", "fixture", ["todo-a", "todo-b"],
        new(2026, 1, 3), "Original title", new(2026, 1, 1), new(2026, 1, 2), "Original summary",
        ["Outcome"], "Synthetic input");

    private sealed class LegacyPort : ILegacyTodoProposalPort
    {
        public IReadOnlyList<TodoProposal> Proposals { get; set; } = [new("Fixture proposal")];
        public List<string> Parsed { get; } = [];
        public List<TodoProposal> Confirmed { get; } = [];
        public Exception? Failure { get; set; }
        public FormatException? ParseFailure { get; set; }
        public IReadOnlyList<TodoProposal> Parse(string modelResponse)
        {
            Parsed.Add(modelResponse);
            if (ParseFailure is not null) throw ParseFailure;
            return Proposals;
        }
        public TodoItem Confirm(TodoProposal proposal)
        {
            Confirmed.Add(proposal);
            if (Failure is not null) throw Failure;
            return new("todo-fixture", proposal.Title, proposal.Description, proposal.Priority, proposal.DueAt, Now, Now);
        }
    }

    private sealed class ArchivePort : IArchiveDraftConfirmation
    {
        public List<ArchiveDraft> Confirmed { get; } = [];
        public Exception? Failure { get; set; }
        public void Confirm(ArchiveDraft draft)
        {
            Confirmed.Add(draft);
            if (Failure is not null) throw Failure;
        }
    }

    private sealed class ConversationFixture : IDisposable
    {
        private readonly string _path = Path.Combine(Path.GetTempPath(), $"fgo-card-contract-{Guid.NewGuid():N}.db");
        public ConversationViewModel Model { get; }
        public ConversationFixture(ILegacyTodoProposalPort? todo, IArchiveDraftConfirmation? archive)
        {
            var database = new RuntimeDatabase(_path, pooling: false);
            new RuntimeDatabaseMigrator(database).Migrate();
            var settings = TestDialogueSettingsStore.WithModelConnection();
            var orchestrator = new ConversationOrchestrator(new UnusedProvider(), new UnusedContent(),
                new SqliteConversationRepository(database), new NoMemory(), new PromptComposer(), TimeProvider.System, settings);
            Model = new(orchestrator, settings, todoProposals: todo, archiveDrafts: archive);
        }
        public void Dispose()
        {
            Model.Dispose();
            foreach (var suffix in new[] { "", "-wal", "-shm" }) File.Delete(_path + suffix);
        }
    }
    private sealed class UnusedProvider : IChatProviderResolver
    {
        public IChatProvider Resolve() => throw new InvalidOperationException("Card presentation must not resolve a model.");
    }
    private sealed class UnusedContent : IConversationContentResolver
    {
        public Task<ContentBinding> ResolveAsync(string servantId, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Card presentation must not resolve content.");
    }
    private sealed class NoMemory : IMemoryRecall
    {
        public MemoryRecallSnapshot Query(MemoryScope scope, string query, int maxItems = 8, int maxChars = 6000) => new(0, []);
    }
    private sealed class TodoRepository : ITodoRepository
    {
        public List<TodoItem> Items { get; } = [];
        public int Writes { get; private set; }
        public void Save(TodoItem todo) { Items.RemoveAll(item => item.Id == todo.Id); Items.Add(todo); Writes++; }
        public TodoItem? Get(string id) => Items.SingleOrDefault(item => item.Id == id);
        public IReadOnlyList<TodoItem> List(TodoStatus? status = null) => Items.Where(item => status is null || item.Status == status).ToArray();
        public IReadOnlyList<TodoItem> ListCompletedOn(DateOnly localDate) => Items.Where(item =>
            item.CompletedAt?.ToLocalTime().Date == localDate.ToDateTime(TimeOnly.MinValue).Date).ToArray();
        public void Delete(string id) => Items.RemoveAll(item => item.Id == id);
        public void ClearAgentTodoData() => Items.Clear();
    }
    private sealed class ArchiveRepository(TodoRepository todos) : IWorkArchiveRepository
    {
        public List<WorkArchive> Items { get; } = [];
        public void Confirm(WorkArchive archive)
        {
            Items.RemoveAll(item => item.ArchiveId == archive.ArchiveId);
            Items.Add(archive);
            foreach (var id in archive.CoveredTodoKeys) todos.Delete(id);
        }
        public WorkArchive? Get(string archiveId) => Items.SingleOrDefault(item => item.ArchiveId == archiveId);
        public IReadOnlyList<WorkArchive> List() => Items.ToArray();
        public IReadOnlyList<string> LoadCoveredTodoKeys(string archiveId) => Get(archiveId)?.CoveredTodoKeys ?? [];
    }
}
