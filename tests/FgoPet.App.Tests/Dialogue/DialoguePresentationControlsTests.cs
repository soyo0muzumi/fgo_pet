using FgoPet.App.Bootstrap;
using FgoPet.App.Dialogue;
using FgoPet.Core.Agents;
using Xunit;

namespace FgoPet.App.Tests.Dialogue;

public sealed class DialoguePresentationControlsTests
{
    [Fact]
    public void Model_picker_exposes_only_nonempty_configured_choices()
    {
        var picker = new DialogueModelSelectionViewModel(new MutableModelAuthority([
            new DialogueModelChoice("  ", "ignored"),
            new DialogueModelChoice("model-a", "Model A"),
            new DialogueModelChoice("model-a", "duplicate"),
            new DialogueModelChoice("model-b", "Model B"),
        ]), "model-b");

        Assert.Equal(["model-a", "model-b"], picker.Models.Select(model => model.Id));
        Assert.Equal("model-b", picker.SelectedModelId);
    }

    [Fact]
    public void Model_switch_is_deferred_while_a_reply_is_generating()
    {
        var picker = new DialogueModelSelectionViewModel(new MutableModelAuthority([
            new DialogueModelChoice("model-a", "Model A"),
            new DialogueModelChoice("model-b", "Model B"),
        ]), "model-a");

        picker.BeginGeneration();

        Assert.False(picker.TrySelect("model-b"));
        Assert.Equal("model-a", picker.SelectedModelId);

        picker.EndGeneration();
        Assert.True(picker.TrySelect("model-b"));
        Assert.Equal("model-b", picker.SelectedModelId);
    }

    [Fact]
    public void Model_switch_updates_the_configured_snapshot_for_future_requests_only()
    {
        var settings = TestDialogueSettingsStore.WithModelConnection();
        var conversation = TestDialogueFakes.CreateConversation(settings);

        Assert.False(conversation.SelectModelForFutureRequests("next-model"));
        Assert.Equal("test-model", settings.Current.ModelConnection!.ModelId);
    }

    [Fact]
    public void Conversation_rejects_a_model_id_outside_the_authoritative_available_set()
    {
        var settings = TestDialogueSettingsStore.WithModelConnection();
        var conversation = TestDialogueFakes.CreateConversation(settings);
        Assert.False(conversation.SelectModelForFutureRequests("invented-model"));
        Assert.Equal("test-model", settings.Current.ModelConnection!.ModelId);
    }

    [Fact]
    public void Composer_is_a_presentation_delegate_over_the_shared_conversation()
    {
        var conversation = TestDialogueFakes.CreateConversation();
        var composer = new DialogueComposerViewModel(conversation);

        composer.InputText = "保留草稿";

        Assert.Equal("保留草稿", conversation.InputText);
        Assert.Same(conversation.SendOrStopCommand, composer.SendOrStopCommand);
        Assert.Same(conversation.StopCommand, composer.StopCommand);

        var changed = new List<string?>();
        composer.PropertyChanged += (_, args) => changed.Add(args.PropertyName);
        conversation.InputText = "新草稿";
        Assert.Contains(nameof(ConversationViewModel.InputText), changed);
        Assert.Contains(nameof(ConversationViewModel.CanSendOrStop), changed);
    }

    [Fact]
    public void Action_card_host_and_tool_drawer_are_presentation_only()
    {
        var conversation = TestDialogueFakes.CreateConversation();
        var cards = new DialogueActionCardHostViewModel(conversation.TodoProposals, conversation.ArchiveDrafts);
        var tools = new DialogueToolDrawerViewModel([
            new DialogueToolOption("todo", "整理成待办", "准备下一条消息的意图"),
        ]);

        Assert.False(cards.IsVisible);
        Assert.False(tools.IsOpen);
        Assert.True(tools.TrySelect("todo"));
        Assert.Equal("todo", tools.SelectedToolId);
        Assert.True(tools.IsOpen);
    }

    [Fact]
    public async Task Project_selector_refreshes_real_catalog_into_session_context()
    {
        var selector = new DialogueProjectSelectionViewModel(new FakeDialogueProjectCatalog(
            new DialogueProjectCatalogResult(
                true,
                [new DialogueProjectOption("target-1", "FGO Pet", "main · 可写", false)])));
        var context = new DialogueSessionContextViewModel();

        await selector.RefreshAsync();

        var project = Assert.Single(selector.Projects);
        Assert.True(selector.TrySelect(project, context));
        Assert.Equal("target-1", context.ProjectId);
        Assert.Equal("FGO Pet", context.ProjectLabel);
        Assert.True(context.TryRemove("project"));
        Assert.Empty(context.ProjectId);
    }
    [Fact]

    public void Session_context_keeps_safe_chips_and_clears_transient_values()
    {
        var context = new DialogueSessionContextViewModel();

        Assert.True(context.TrySetProject("project-1", "FGO Pet"));
        Assert.True(context.TryAddAttachment(@"C:\private\plan.md"));
        Assert.True(context.TrySetIntent("todo"));

        var request = context.ToRequestContext();
        Assert.Equal("project-1", request.ProjectId);
        Assert.Equal(["plan.md"], request.AttachmentNames);
        Assert.Equal("todo", request.IntentId);
        Assert.Contains(context.Chips, chip => chip.Label == "项目 · FGO Pet");
        Assert.DoesNotContain(context.Chips, chip => chip.Label.Contains("private", StringComparison.OrdinalIgnoreCase));

        context.ClearTransient();

        Assert.Equal("FGO Pet", context.ProjectLabel);
        Assert.Empty(context.AttachmentNames);
        Assert.Empty(context.IntentId);
        Assert.Single(context.Chips);
    }
    [Fact]
    public void Model_picker_refreshes_from_the_authoritative_source()
    {
        var source = new MutableModelAuthority([new DialogueModelChoice("model-a", "Model A")]);
        var picker = new DialogueModelSelectionViewModel(source, "model-a");

        source.Replace([new DialogueModelChoice("model-b", "Model B")]);

        Assert.Equal(["model-b"], picker.Models.Select(model => model.Id));
        Assert.False(picker.TrySelect("model-a"));
        Assert.True(picker.TrySelect("model-b"));
    }

    [Fact]
    public async Task Project_catalog_preserves_safe_descriptor_metadata_for_the_project_card()
    {
        var refreshed = new DateTimeOffset(2026, 9, 9, 8, 30, 0, TimeSpan.Zero);
        var target = new AgentTargetDescriptor(
            "target-1",
            "FGO Pet",
            isReadOnly: false,
            projectName: "FGO Pet",
            branches: new[] { "main", "feature/x" },
            currentBranch: "main",
            revision: "abc123",
            access: "workspace-write",
            source: "local-adapter",
            refreshedAtUtc: refreshed,
            contextVersion: "context-1");
        var catalog = new AgentDialogueProjectCatalog(new FakeAgentTargetCatalog(
            new AgentTargetCatalogResult(AgentTargetCatalogStatus.Available, new[] { target })));

        var result = await catalog.ListAsync();

        var project = Assert.Single(result.Projects);
        Assert.Equal(new[] { "main", "feature/x" }, project.Branches);
        Assert.Equal("workspace-write", project.Access);
        Assert.Equal("local-adapter", project.Source);
        Assert.Equal(refreshed, project.RefreshedAtUtc);
        Assert.Equal("context-1", project.ContextVersion);
        Assert.Contains("来源 本地助手", project.Detail, StringComparison.Ordinal);
        Assert.Contains($"刷新 {refreshed.ToLocalTime():HH\\:mm}", project.Detail, StringComparison.Ordinal);
        Assert.DoesNotContain("C:\\work", project.Detail, StringComparison.OrdinalIgnoreCase);
    }

    private sealed class FakeDialogueProjectCatalog(DialogueProjectCatalogResult result) : IDialogueProjectCatalog
    {
        public Task<DialogueProjectCatalogResult> ListAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(result);
    }
    private sealed class FakeAgentTargetCatalog(AgentTargetCatalogResult result) : IAgentTargetCatalog
    {
        public Task<AgentTargetCatalogResult> ListAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(result);
    }
    private sealed class MutableModelAuthority(IEnumerable<DialogueModelChoice> initial) : IConfiguredModelAuthority
    {
        private IReadOnlyList<DialogueModelChoice> _models = Normalize(initial);
        public IReadOnlyList<DialogueModelChoice> AvailableModels => _models;
        public event EventHandler? Changed;
        public bool IsAvailable(string modelId) => _models.Any(model => model.Id == modelId);
        public void Replace(IEnumerable<DialogueModelChoice> models)
        {
            _models = Normalize(models);
            Changed?.Invoke(this, EventArgs.Empty);
        }

        private static IReadOnlyList<DialogueModelChoice> Normalize(IEnumerable<DialogueModelChoice> models) => models
            .Where(model => !string.IsNullOrWhiteSpace(model.Id))
            .GroupBy(model => model.Id.Trim(), StringComparer.Ordinal)
            .Select(group => group.First() with { Id = group.Key })
            .ToArray();
    }
}
