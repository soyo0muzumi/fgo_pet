using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FgoPet.App.Privacy;
using FgoPet.Core.Memory;
using FgoPet.Memory.Settings;
using Microsoft.Extensions.Logging;

namespace FgoPet.App.Memory;

public sealed partial class MemoryViewModel : ObservableObject, IDisposable
{
    private readonly MemoryCandidateService _memories;
    private readonly IUserDataExporter? _export;
    private readonly IUserDataDeleter? _deletion;
    private readonly IMemorySettingsStore? _settings;
    private readonly ILogger<MemoryViewModel>? _logger;
    private readonly Func<string, CancellationToken, Task<(IReadOnlyList<MemoryCandidate> Candidates, IReadOnlyList<StoredMemory> Memories)>> _loadMemories;
    private CancellationTokenSource? _refreshCancellation;
    private long _refreshGeneration;
    private bool _disposed;

    public MemoryViewModel(
        MemoryCandidateService memories,
        IUserDataExporter? export = null,
        IUserDataDeleter? deletion = null,
        IMemorySettingsStore? settings = null,
        ILogger<MemoryViewModel>? logger = null)
    {
        _memories = memories ?? throw new ArgumentNullException(nameof(memories));
        _export = export;
        _deletion = deletion;
        _settings = settings;
        _logger = logger;
        _loadMemories = async (servantId, token) => (
            await memories.ListCandidatesAsync(servantId, token),
            await memories.ListAllAsync(servantId, token));
        _memoryEnabled = settings?.Load().Enabled ?? true;
        RefreshCommand = new AsyncRelayCommand(RefreshAsync);
        ApproveCandidateCommand = new AsyncRelayCommand(() => ReviewCandidateAsync(MemoryReviewAction.Approve));
        RejectCandidateCommand = new AsyncRelayCommand(() => ReviewCandidateAsync(MemoryReviewAction.Reject));
        EditCandidateCommand = new AsyncRelayCommand(() => ReviewCandidateAsync(MemoryReviewAction.Edit));
        DisableMemoryCommand = new AsyncRelayCommand(() => ReviewMemoryAsync(MemoryReviewAction.Disable));
        DeleteMemoryCommand = new AsyncRelayCommand(() => ReviewMemoryAsync(MemoryReviewAction.Delete));
        EditMemoryCommand = new AsyncRelayCommand(() => ReviewMemoryAsync(MemoryReviewAction.Edit));
        ExportCommand = new AsyncRelayCommand(ExportAsync);
        DeleteAllCommand = new AsyncRelayCommand(DeleteAllAsync);
    }

    // Internal read seam keeps delayed/failing queries testable without changing
    // module contracts, persistence, or the public construction path.
    internal MemoryViewModel(
        MemoryCandidateService memories,
        Func<string, CancellationToken, Task<(IReadOnlyList<MemoryCandidate> Candidates, IReadOnlyList<StoredMemory> Memories)>> loadMemories)
        : this(memories)
    {
        _loadMemories = loadMemories;
    }

    public ObservableCollection<MemoryCandidate> Candidates { get; } = new();
    public ObservableCollection<StoredMemory> StoredMemories { get; } = new();

    [ObservableProperty]
    private string _activeServantId = string.Empty;

    [ObservableProperty]
    private MemoryCandidate? _selectedCandidate;

    [ObservableProperty]
    private StoredMemory? _selectedMemory;

    [ObservableProperty]
    private string _candidateEditText = string.Empty;

    [ObservableProperty]
    private string _memoryEditText = string.Empty;

    [ObservableProperty]
    private string _exportPath = "fgo-pet-export.zip";

    [ObservableProperty]
    private string _statusText = "请先选择角色。";

    [ObservableProperty]
    private string _candidatesStatusText = "请先选择角色。";

    [ObservableProperty]
    private string _storedMemoriesStatusText = "请先选择角色。";

    [ObservableProperty]
    private bool _isLoading;

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

    partial void OnMemoryEnabledChanged(bool value)
    {
        if (_settings is not null)
        {
            _settings.Save(_settings.Load() with { Enabled = value });
        }
    }

    public void SetActiveServant(string? servantId)
    {
        if (_disposed) return;
        var normalized = servantId?.Trim() ?? string.Empty;
        if (string.Equals(ActiveServantId, normalized, StringComparison.Ordinal)) return;
        ActiveServantId = normalized;
        RefreshCommand.Execute(null);
    }

    partial void OnActiveServantIdChanged(string value)
    {
        _refreshGeneration++;
        _refreshCancellation?.Cancel();
        ClearListsAndSelection();
        IsLoading = false;
        StatusText = CandidatesStatusText = StoredMemoriesStatusText =
            string.IsNullOrWhiteSpace(value) ? "请先选择角色。" : "等待刷新。";
    }

    public async Task RefreshAsync()
    {
        if (_disposed) return;
        var generation = ++_refreshGeneration;
        _logger?.LogDebug("Memory list refresh {Generation} started", generation);
        _refreshCancellation?.Cancel();
        var servantId = ActiveServantId;
        ClearListsAndSelection();
        if (string.IsNullOrWhiteSpace(servantId))
        {
            IsLoading = false;
            StatusText = CandidatesStatusText = StoredMemoriesStatusText = "请先选择角色。";
            _logger?.LogDebug("Memory list refresh {Generation} skipped: {Code}", generation, "no_active_role");
            return;
        }

        using var cancellation = new CancellationTokenSource();
        _refreshCancellation = cancellation;
        IsLoading = true;
        StatusText = "正在加载当前角色的数据…";
        CandidatesStatusText = StoredMemoriesStatusText = "正在加载记忆…";
        try
        {
            await LoadMemoriesAsync();
            if (!IsCurrent()) return;
            IsLoading = false;
            StatusText = $"{CandidatesStatusText} · {StoredMemoriesStatusText}";
        }
        finally
        {
            _logger?.LogDebug("Memory list refresh {Generation} finished; current: {Current}", generation, IsCurrent());
            if (ReferenceEquals(_refreshCancellation, cancellation)) _refreshCancellation = null;
        }

        bool IsCurrent() => generation == _refreshGeneration && !cancellation.IsCancellationRequested
            && string.Equals(servantId, ActiveServantId, StringComparison.Ordinal);

        async Task LoadMemoriesAsync()
        {
            try
            {
                var snapshot = await _loadMemories(servantId, cancellation.Token);
                if (!IsCurrent()) return;
                foreach (var candidate in snapshot.Candidates) Candidates.Add(candidate);
                foreach (var memory in snapshot.Memories) StoredMemories.Add(memory);
                CandidatesStatusText = Candidates.Count == 0 ? "当前角色暂无待审核候选。" : $"候选 {Candidates.Count} 条";
                StoredMemoriesStatusText = StoredMemories.Count == 0 ? "当前角色暂无已确认记忆。" : $"已确认记忆 {StoredMemories.Count} 条";
            }
            catch (OperationCanceledException) when (!IsCurrent()) { }
            catch (Exception)
            {
                _logger?.LogWarning("Memory list refresh {Generation}: {Code}", generation, "memory_load_failed");
                if (IsCurrent()) CandidatesStatusText = StoredMemoriesStatusText = "记忆加载失败，请刷新重试。";
            }
        }
    }

    private void ClearListsAndSelection()
    {
        SelectedCandidate = null;
        SelectedMemory = null;
        Candidates.Clear();
        StoredMemories.Clear();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _refreshGeneration++;
        _refreshCancellation?.Cancel();
        IsLoading = false;
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

    partial void OnSelectedCandidateChanged(MemoryCandidate? value) =>
        CandidateEditText = value?.Text ?? string.Empty;

    partial void OnSelectedMemoryChanged(StoredMemory? value) =>
        MemoryEditText = value?.Text ?? string.Empty;
}
