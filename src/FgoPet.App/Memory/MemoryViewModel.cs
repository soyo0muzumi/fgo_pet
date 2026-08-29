using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FgoPet.App.Privacy;
using FgoPet.Core.Dialogue;
using FgoPet.Core.Memory;
using FgoPet.Core.Settings;
using FgoPet.Infrastructure.Dialogue;

namespace FgoPet.App.Memory;

public sealed partial class MemoryViewModel : ObservableObject
{
    private readonly MemoryCandidateService _memories;
    private readonly UserDataExportService? _export;
    private readonly UserDataDeletionService? _deletion;
    private readonly IAppSettingsStore? _settings;
    private readonly SqliteConversationRepository? _conversations;

    public MemoryViewModel(
        MemoryCandidateService memories,
        UserDataExportService? export = null,
        UserDataDeletionService? deletion = null,
        IAppSettingsStore? settings = null,
        SqliteConversationRepository? conversations = null)
    {
        _memories = memories ?? throw new ArgumentNullException(nameof(memories));
        _export = export;
        _deletion = deletion;
        _settings = settings;
        _conversations = conversations;
        _memoryEnabled = settings?.Load().MemoryEnabled ?? true;
        RefreshCommand = new AsyncRelayCommand(RefreshAsync);
        ApproveCandidateCommand = new AsyncRelayCommand(() => ReviewCandidateAsync(MemoryReviewAction.Approve));
        RejectCandidateCommand = new AsyncRelayCommand(() => ReviewCandidateAsync(MemoryReviewAction.Reject));
        EditCandidateCommand = new AsyncRelayCommand(() => ReviewCandidateAsync(MemoryReviewAction.Edit));
        DisableMemoryCommand = new AsyncRelayCommand(() => ReviewMemoryAsync(MemoryReviewAction.Disable));
        DeleteMemoryCommand = new AsyncRelayCommand(() => ReviewMemoryAsync(MemoryReviewAction.Delete));
        EditMemoryCommand = new AsyncRelayCommand(() => ReviewMemoryAsync(MemoryReviewAction.Edit));
        ExportCommand = new AsyncRelayCommand(ExportAsync);
        DeleteAllCommand = new AsyncRelayCommand(DeleteAllAsync);
        DeleteConversationCommand = new AsyncRelayCommand(DeleteConversationAsync);
    }

    public ObservableCollection<MemoryCandidate> Candidates { get; } = new();
    public ObservableCollection<StoredMemory> StoredMemories { get; } = new();
    public ObservableCollection<Conversation> Conversations { get; } = new();

    [ObservableProperty]
    private string _activeServantId = string.Empty;

    [ObservableProperty]
    private MemoryCandidate? _selectedCandidate;

    [ObservableProperty]
    private StoredMemory? _selectedMemory;

    [ObservableProperty]
    private Conversation? _selectedConversation;

    [ObservableProperty]
    private string _candidateEditText = string.Empty;

    [ObservableProperty]
    private string _memoryEditText = string.Empty;

    [ObservableProperty]
    private string _exportPath = "fgo-pet-export.zip";

    [ObservableProperty]
    private string _statusText = "选择从者后管理记忆。";

    [ObservableProperty]
    private bool _memoryEnabled;

    public IAsyncRelayCommand RefreshCommand { get; }
    public IAsyncRelayCommand ApproveCandidateCommand { get; }
    public IAsyncRelayCommand RejectCandidateCommand { get; }
    public IAsyncRelayCommand EditCandidateCommand { get; }
    public IAsyncRelayCommand DisableMemoryCommand { get; }
    public IAsyncRelayCommand DeleteMemoryCommand { get; }
    public IAsyncRelayCommand EditMemoryCommand { get; }
    public IAsyncRelayCommand ExportCommand { get; }
    public IAsyncRelayCommand DeleteAllCommand { get; }
    public IAsyncRelayCommand DeleteConversationCommand { get; }

    partial void OnMemoryEnabledChanged(bool value)
    {
        if (_settings is not null)
        {
            _settings.Save(_settings.Load() with { MemoryEnabled = value });
        }
    }

    public void SetActiveServant(string? servantId)
    {
        ActiveServantId = servantId?.Trim() ?? string.Empty;
        _ = RefreshAsync();
    }

    public async Task RefreshAsync()
    {
        Candidates.Clear();
        StoredMemories.Clear();
        Conversations.Clear();
        if (string.IsNullOrWhiteSpace(ActiveServantId))
        {
            return;
        }

        foreach (var candidate in await _memories.ListCandidatesAsync(ActiveServantId, CancellationToken.None))
        {
            Candidates.Add(candidate);
        }

        foreach (var memory in await _memories.ListAllAsync(ActiveServantId, CancellationToken.None))
        {
            StoredMemories.Add(memory);
        }

        if (_conversations is not null)
        {
            foreach (var conversation in _conversations.ListConversations(ActiveServantId))
            {
                Conversations.Add(conversation);
            }
        }

        StatusText = $"会话 {Conversations.Count} 个 · 候选 {Candidates.Count} 条 · 已确认记忆 {StoredMemories.Count} 条";
    }

    private async Task ReviewCandidateAsync(MemoryReviewAction action)
    {
        if (SelectedCandidate is null || string.IsNullOrWhiteSpace(ActiveServantId)) return;
        var text = action == MemoryReviewAction.Edit ? CandidateEditText : null;
        await _memories.ReviewAsync(ActiveServantId, SelectedCandidate.CandidateId, action, text, CancellationToken.None);
        await RefreshAsync();
    }

    private async Task ReviewMemoryAsync(MemoryReviewAction action)
    {
        if (SelectedMemory is null || string.IsNullOrWhiteSpace(ActiveServantId)) return;
        var text = action == MemoryReviewAction.Edit ? MemoryEditText : null;
        await _memories.ReviewMemoryAsync(ActiveServantId, SelectedMemory.MemoryId, action, text, CancellationToken.None);
        await RefreshAsync();
    }

    private async Task ExportAsync()
    {
        if (_export is null)
        {
            StatusText = "导出服务不可用。";
            return;
        }

        await _export.ExportAsync(ExportPath, CancellationToken.None);
        StatusText = $"已导出：{Path.GetFullPath(ExportPath)}";
    }

    private async Task DeleteAllAsync()
    {
        if (_deletion is null)
        {
            StatusText = "删除服务不可用。";
            return;
        }

        await _deletion.DeleteAllAsync(CancellationToken.None);
        await RefreshAsync();
        StatusText = "已删除全部用户数据（不含 Phase 2 专注/羁绊历史）。";
    }

    private async Task DeleteConversationAsync()
    {
        if (_deletion is null || SelectedConversation is null) return;
        await _deletion.DeleteConversationAsync(
            SelectedConversation.ConversationId,
            ActiveServantId,
            CancellationToken.None);
        await RefreshAsync();
    }

    partial void OnSelectedCandidateChanged(MemoryCandidate? value) =>
        CandidateEditText = value?.Text ?? string.Empty;

    partial void OnSelectedMemoryChanged(StoredMemory? value) =>
        MemoryEditText = value?.Text ?? string.Empty;
}
