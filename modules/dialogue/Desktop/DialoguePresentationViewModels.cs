using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using FgoPet.App.Settings;
using FgoPet.Core.Dialogue;

namespace FgoPet.App.Dialogue;

public sealed record DialogueModelChoice(string Id, string DisplayName);

public interface IConfiguredModelAuthority
{
    IReadOnlyList<DialogueModelChoice> AvailableModels { get; }
    bool IsAvailable(string modelId);
    event EventHandler? Changed;
}

public sealed class DialogueModelConnectionSource : IConfiguredModelAuthority
{
    private readonly ModelConnectionViewModel _connection;
    public DialogueModelConnectionSource(ModelConnectionViewModel connection)
    {
        _connection = connection ?? throw new ArgumentNullException(nameof(connection));
        _connection.PropertyChanged += OnConnectionPropertyChanged;
    }
    public event EventHandler? Changed;
    public IReadOnlyList<DialogueModelChoice> AvailableModels => _connection.AvailableModels
        .Where(model => !string.IsNullOrWhiteSpace(model.Id))
        .GroupBy(model => model.Id.Trim(), StringComparer.Ordinal)
        .Select(group => new DialogueModelChoice(group.Key, group.First().DisplayName))
        .ToArray();
    public bool IsAvailable(string modelId) => AvailableModels.Any(model => model.Id == modelId);
    private void OnConnectionPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ModelConnectionViewModel.AvailableModels)) Changed?.Invoke(this, EventArgs.Empty);
    }
}

/// <summary>Presentation-only model choices supplied by the configured connection.</summary>
public sealed class DialogueModelSelectionViewModel : INotifyPropertyChanged
{
    private readonly IConfiguredModelAuthority _authority;
    private IReadOnlyList<DialogueModelChoice> _models;
    private string? _selectedModelId;
    private bool _isGenerating;

    public DialogueModelSelectionViewModel(IConfiguredModelAuthority authority, string? selectedModelId)
    {
        _authority = authority ?? throw new ArgumentNullException(nameof(authority));
        _authority.Changed += OnAuthorityChanged;
        _models = authority.AvailableModels;
        _selectedModelId = _models.Any(model => model.Id == selectedModelId) ? selectedModelId : _models.FirstOrDefault()?.Id;
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    public event Action<string>? ModelSelected;

    public IReadOnlyList<DialogueModelChoice> Models => _models;

    public string? SelectedModelId
    {
        get => _selectedModelId;
        private set
        {
            if (_selectedModelId == value) return;
            _selectedModelId = value;
            PropertyChanged?.Invoke(this, new(nameof(SelectedModelId)));
        }
    }

    public bool IsGenerating
    {
        get => _isGenerating;
        private set
        {
            if (_isGenerating == value) return;
            _isGenerating = value;
            PropertyChanged?.Invoke(this, new(nameof(IsGenerating)));
        }
    }

    public void BeginGeneration() => IsGenerating = true;

    public void EndGeneration() => IsGenerating = false;

    public bool TrySelect(string modelId)
    {
        if (IsGenerating) return false;
        var choice = _models.FirstOrDefault(model => model.Id == modelId);
        if (choice is null) return false;
        SelectedModelId = choice.Id;
        ModelSelected?.Invoke(choice.Id);
        return true;
    }

    private void OnAuthorityChanged(object? sender, EventArgs e)
    {
        _models = _authority.AvailableModels;
        if (_selectedModelId is not null && !_authority.IsAvailable(_selectedModelId))
        {
            SelectedModelId = _models.FirstOrDefault()?.Id;
        }
        PropertyChanged?.Invoke(this, new(nameof(Models)));
    }
}

/// <summary>Delegates composer state and commands to the shared conversation owner.</summary>
public sealed class DialogueComposerViewModel : ObservableObject
{
    public DialogueComposerViewModel(ConversationViewModel conversation)
    {
        Conversation = conversation ?? throw new ArgumentNullException(nameof(conversation));
        Conversation.PropertyChanged += OnConversationPropertyChanged;
    }

    public ConversationViewModel Conversation { get; }
    public string InputText { get => Conversation.InputText; set => Conversation.InputText = value; }
    public bool CanSendOrStop => Conversation.CanSendOrStop;
    public string ActionLabel => Conversation.ActionLabel;
    public IAsyncRelayCommand SendOrStopCommand => Conversation.SendOrStopCommand;
    public IRelayCommand StopCommand => Conversation.StopCommand;

    private void OnConversationPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(ConversationViewModel.InputText)
            or nameof(ConversationViewModel.CanSendOrStop)
            or nameof(ConversationViewModel.ActionLabel)
            or nameof(ConversationViewModel.IsStreaming)) OnPropertyChanged(e.PropertyName);
    }
}

public sealed record DialogueContextChip(string Id, string Label);

/// <summary>
/// Transient, presentation-owned context for the shared conversation. It exposes
/// only safe display names to the prompt layer and never creates Todo/Focus data.
/// </summary>
public sealed class HistorySourceViewModel(FgoPet.Core.Dialogue.HistoryHit source, Action open)
{
    public string Title => source.Title;
    public string Detail => source.Anchor.CreatedAtUtc.ToLocalTime().ToString("MM-dd HH:mm") + " · " + source.Excerpt;
    public IRelayCommand OpenCommand { get; } = new RelayCommand(open);
}

public sealed class DialogueSessionContextViewModel : ObservableObject
{
    private static readonly IReadOnlyDictionary<string, string> IntentLabels =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["todo"] = "整理成待办",
            ["focus"] = "开始专注",
        };

    private readonly ObservableCollection<string> _attachmentNames = new();
    private string _projectId = string.Empty;
    private string _projectLabel = string.Empty;
    private string _intentId = string.Empty;

    public ObservableCollection<DialogueContextChip> Chips { get; } = new();

    public string ProjectId => _projectId;
    public string ProjectLabel => _projectLabel;
    public string ProjectSelectionText => _projectLabel.Length == 0 ? "尚未选择项目" : _projectLabel;
    public IReadOnlyList<string> AttachmentNames => _attachmentNames;
    public string IntentId => _intentId;
    public string IntentLabel => IntentLabels.TryGetValue(_intentId, out var label) ? label : string.Empty;
    public bool HasContext => Chips.Count > 0;

    public bool TrySetProject(string projectId, string projectLabel)
    {
        if (string.IsNullOrWhiteSpace(projectId)
            || projectId.Length > 128
            || string.IsNullOrWhiteSpace(projectLabel)
            || projectLabel.Length > 160)
        {
            return false;
        }

        _projectId = projectId.Trim();
        _projectLabel = projectLabel.Trim();
        RefreshChips();
        return true;
    }

    public bool TryAddAttachment(string displayName)
    {
        if (string.IsNullOrWhiteSpace(displayName) || _attachmentNames.Count >= 8)
        {
            return false;
        }

        var safeName = Path.GetFileName(displayName.Trim());
        if (string.IsNullOrWhiteSpace(safeName)
            || safeName.Length > 160
            || _attachmentNames.Contains(safeName, StringComparer.OrdinalIgnoreCase))
        {
            return false;
        }

        _attachmentNames.Add(safeName);
        RefreshChips();
        return true;
    }

    public bool TrySetIntent(string intentId)
    {
        if (string.IsNullOrWhiteSpace(intentId) || !IntentLabels.ContainsKey(intentId.Trim()))
        {
            return false;
        }

        _intentId = intentId.Trim();
        RefreshChips();
        return true;
    }

    public bool TryRemove(string chipId)
    {
        if (string.Equals(chipId, "project", StringComparison.Ordinal))
        {
            if (_projectId.Length == 0) return false;
            _projectId = string.Empty;
            _projectLabel = string.Empty;
            RefreshChips();
            return true;
        }

        if (string.Equals(chipId, "intent", StringComparison.Ordinal))
        {
            if (_intentId.Length == 0) return false;
            _intentId = string.Empty;
            RefreshChips();
            return true;
        }

        const string attachmentPrefix = "attachment:";
        if (!chipId.StartsWith(attachmentPrefix, StringComparison.Ordinal))
        {
            return false;
        }

        var attachmentName = chipId[attachmentPrefix.Length..];
        var index = Enumerable.Range(0, _attachmentNames.Count)
            .FirstOrDefault(candidate => string.Equals(
                _attachmentNames[candidate],
                attachmentName,
                StringComparison.OrdinalIgnoreCase), -1);
        if (index < 0)
        {
            return false;
        }

        _attachmentNames.RemoveAt(index);
        RefreshChips();
        return true;
    }

    public ConversationRequestContext ToRequestContext() => new(
        _projectId,
        _projectLabel,
        _attachmentNames,
        _intentId,
        IntentLabel);

    public void ClearTransient()
    {
        _attachmentNames.Clear();
        _intentId = string.Empty;
        RefreshChips();
    }

    public void Clear()
    {
        _projectId = string.Empty;
        _projectLabel = string.Empty;
        _attachmentNames.Clear();
        _intentId = string.Empty;
        RefreshChips();
    }

    private void RefreshChips()
    {
        Chips.Clear();
        if (_projectLabel.Length > 0)
        {
            Chips.Add(new DialogueContextChip("project", $"项目 · {_projectLabel}"));
        }

        foreach (var attachmentName in _attachmentNames)
        {
            Chips.Add(new DialogueContextChip($"attachment:{attachmentName}", $"附件 · {attachmentName}"));
        }

        if (IntentLabel.Length > 0)
        {
            Chips.Add(new DialogueContextChip("intent", $"意图 · {IntentLabel}"));
        }

        OnPropertyChanged(nameof(ProjectId));
        OnPropertyChanged(nameof(ProjectLabel));
        OnPropertyChanged(nameof(ProjectSelectionText));
        OnPropertyChanged(nameof(AttachmentNames));
        OnPropertyChanged(nameof(IntentId));
        OnPropertyChanged(nameof(IntentLabel));
        OnPropertyChanged(nameof(HasContext));
    }
}

public sealed class DialogueMessageActionsViewModel
{
    public DialogueMessageActionsViewModel(ConversationTurnViewModel turn) => Turn = turn ?? throw new ArgumentNullException(nameof(turn));
    public ConversationTurnViewModel Turn { get; }
    public bool CanCopy => !string.IsNullOrWhiteSpace(Turn.Text);
    public event Action<string>? CopyRequested;
    public void RequestCopy()
    {
        if (CanCopy) CopyRequested?.Invoke(Turn.Text);
    }
}

public sealed class DialogueActionCardHostViewModel
{
    public DialogueActionCardHostViewModel(
        ObservableCollection<FgoPet.App.ViewModels.TodoProposalViewModel> todoProposals,
        ObservableCollection<FgoPet.App.ViewModels.ArchiveDraftViewModel> archiveDrafts)
    {
        TodoProposals = todoProposals ?? throw new ArgumentNullException(nameof(todoProposals));
        ArchiveDrafts = archiveDrafts ?? throw new ArgumentNullException(nameof(archiveDrafts));
    }

    public ObservableCollection<FgoPet.App.ViewModels.TodoProposalViewModel> TodoProposals { get; }
    public ObservableCollection<FgoPet.App.ViewModels.ArchiveDraftViewModel> ArchiveDrafts { get; }
    public bool IsVisible => TodoProposals.Count > 0 || ArchiveDrafts.Count > 0;
}

public sealed record DialogueToolOption(string Id, string Label, string Description);

/// <summary>Presentation intent only; selecting a tool never creates or dispatches business state.</summary>
public sealed class DialogueToolDrawerViewModel
{
    public DialogueToolDrawerViewModel(IEnumerable<DialogueToolOption>? options = null)
    {
        Options = (options ?? new[]
        {
            new DialogueToolOption("todo", "整理成待办", "先形成提案，确认后才创建待办"),
            new DialogueToolOption("focus", "开始专注", "准备开始专注，不会自动启动计时"),
        }).Where(option => !string.IsNullOrWhiteSpace(option.Id)).ToArray();
    }
    public IReadOnlyList<DialogueToolOption> Options { get; }
    public bool IsOpen { get; private set; }
    public string? SelectedToolId { get; private set; }
    public event Action<string>? ToolSelected;
    public void Toggle() => IsOpen = !IsOpen;
    public void Close() => IsOpen = false;
    public bool TrySelect(string id)
    {
        if (!Options.Any(option => option.Id == id)) return false;
        SelectedToolId = id;
        IsOpen = true;
        ToolSelected?.Invoke(id);
        return true;
    }
}
