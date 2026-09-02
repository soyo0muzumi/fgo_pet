using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using FgoPet.App.Services;
using FgoPet.Core.Agents;
using FgoPet.Core.Settings;

namespace FgoPet.App.ViewModels;

public sealed partial class AgentConnectionItemViewModel : ObservableObject
{
    public AgentConnectionItemViewModel(PersistedAgentConnection connection, bool isEnabled)
    {
        Connection = connection ?? throw new ArgumentNullException(nameof(connection));
        _isEnabled = isEnabled;
    }

    public PersistedAgentConnection Connection { get; }
    public string SourceType => Connection.SourceType;
    public string DisplayName => Connection.DisplayName;
    public string Version => Connection.Version;
    public string LastEventText => Connection.LastEventAtUtc?.ToLocalTime().ToString("yyyy-MM-dd HH:mm") ?? "尚无事件";
    public int PendingCount => Connection.PendingCount;
    public IReadOnlyList<AgentProjectTarget> Targets => Connection.Capabilities.ProjectTargets;

    [ObservableProperty]
    private bool _isEnabled;
}

public sealed class AgentPendingSourceViewModel
{
    public AgentPendingSourceViewModel(AgentPendingSource source)
    {
        Source = source ?? throw new ArgumentNullException(nameof(source));
    }

    public AgentPendingSource Source { get; }
    public string RequestId => Source.RequestId;
    public string SourceType => Source.SourceType;
    public string SourceInstanceId => Source.SourceInstanceId;
    public string DisplayName => Source.DisplayName;
    public string AdapterVersion => Source.AdapterVersion;
    public string RequestedAtText => Source.RequestedAtUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm");
    public string ExpiresAtText => Source.ExpiresAtUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm");
}

public sealed class AgentMaintenanceCounterViewModel
{
    public AgentMaintenanceCounterViewModel(AgentMaintenanceCounter counter)
    {
        Counter = counter ?? throw new ArgumentNullException(nameof(counter));
    }

    public AgentMaintenanceCounter Counter { get; }
    public string Name => Counter.Name switch
    {
        "relay_dispatch_receipts" => "Relay 调度回执",
        "relay_event_watermarks" => "Relay 事件水位",
        "relay_inbound_queue" => "Relay 入站队列",
        "relay_outbound_queue" => "Relay 出站队列",
        "relay_archive_tombstones" => "Relay 归档墓碑",
        "adapter_journal" => "适配器执行日志",
        _ => Counter.Name,
    };
    public string UsageText => $"{Counter.Used} / {Counter.Limit}";
    public string ArchivableText => Counter.Archivable > 0
        ? $"可归档 {Counter.Archivable} 条"
        : "暂无可归档记录";
    public string CapacityText => Counter.IsNearCapacity ? "接近容量上限" : "容量正常";
    public string CapacityAccent => Counter.IsNearCapacity ? "#FFFFB84D" : "#FF70E7F5";
}

public sealed partial class AgentApprovedSourceViewModel : ObservableObject
{
    private AgentApprovedSource _source;
    private readonly HashSet<string> _unresolvedTargetIds = new(StringComparer.Ordinal);
    private readonly List<string> _unresolvedTargetOrder = new();
    private IReadOnlyList<AgentTargetDescriptor> _catalog = Array.Empty<AgentTargetDescriptor>();
    private bool _hasCatalog;
    private bool _isDirty;
    private bool _suppressDirtyTracking;

    public AgentApprovedSourceViewModel(AgentApprovedSource source)
    {
        _source = source ?? throw new ArgumentNullException(nameof(source));
        _isEnabled = source.Enabled;
        SetUnresolvedTargets(source.AllowedTargetIds);
    }

    public AgentApprovedSource Source => _source;
    public string SourceType => _source.SourceType;
    public string SourceInstanceId => _source.SourceInstanceId;
    public string DisplayName => _source.DisplayName;
    public string AdapterVersion => _source.AdapterVersion;
    public ObservableCollection<AgentTargetOptionViewModel> Targets { get; } = new();
    public bool HasTargets => Targets.Count > 0;
    public bool HasUnresolvedTargets => _unresolvedTargetIds.Count > 0;
    public IReadOnlyList<string> AllowedTargetIds
    {
        get
        {
            var targetIds = Targets
                .Where(target => target.IsSelected)
                .Select(target => target.TargetId)
                .Concat(_unresolvedTargetOrder)
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            return targetIds;
        }
    }
    public string OnlineText => _source.IsOnline ? "在线" : "离线";

    public void UpdateStatus(AgentApprovedSource source)
    {
        _source = source ?? throw new ArgumentNullException(nameof(source));
        if (!_isDirty)
        {
            _suppressDirtyTracking = true;
            try
            {
                IsEnabled = source.Enabled;
                if (_hasCatalog)
                {
                    ApplyCatalog(_catalog);
                }
                else
                {
                    ClearTargets();
                    SetUnresolvedTargets(source.AllowedTargetIds);
                }
            }
            finally
            {
                _suppressDirtyTracking = false;
            }
        }

        OnPropertyChanged(nameof(Source));
        OnPropertyChanged(nameof(SourceType));
        OnPropertyChanged(nameof(SourceInstanceId));
        OnPropertyChanged(nameof(DisplayName));
        OnPropertyChanged(nameof(AdapterVersion));
        OnPropertyChanged(nameof(AllowedTargetIds));
        OnPropertyChanged(nameof(HasTargets));
        OnPropertyChanged(nameof(HasUnresolvedTargets));
        OnPropertyChanged(nameof(OnlineText));
    }

    public void ApplyCatalog(IReadOnlyList<AgentTargetDescriptor> catalog)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        if (_isDirty) return;

        _catalog = catalog.ToArray();
        _hasCatalog = true;
        var persistedTargetIds = _source.AllowedTargetIds
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .ToArray();
        var matchedTargetIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var catalogTargetIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        _suppressDirtyTracking = true;
        try
        {
            ClearTargets();
            _unresolvedTargetIds.Clear();
            _unresolvedTargetOrder.Clear();

            foreach (var target in _catalog)
            {
                if (string.IsNullOrWhiteSpace(target.TargetId)
                    || !catalogTargetIds.Add(target.TargetId))
                {
                    continue;
                }

                var isSelected = persistedTargetIds.Any(id =>
                    string.Equals(id, target.TargetId, StringComparison.OrdinalIgnoreCase));
                if (isSelected)
                {
                    matchedTargetIds.Add(target.TargetId);
                }

                var option = new AgentTargetOptionViewModel(target, isSelected);
                option.PropertyChanged += OnTargetPropertyChanged;
                Targets.Add(option);
            }

            foreach (var targetId in persistedTargetIds)
            {
                if (!matchedTargetIds.Contains(targetId) && _unresolvedTargetIds.Add(targetId))
                {
                    _unresolvedTargetOrder.Add(targetId);
                }
            }
        }
        finally
        {
            _suppressDirtyTracking = false;
        }

        OnPropertyChanged(nameof(AllowedTargetIds));
        OnPropertyChanged(nameof(HasTargets));
        OnPropertyChanged(nameof(HasUnresolvedTargets));
    }

    public bool RemoveUnresolvedTargets()
    {
        if (_unresolvedTargetIds.Count == 0) return false;

        _unresolvedTargetIds.Clear();
        _unresolvedTargetOrder.Clear();
        _isDirty = true;
        OnPropertyChanged(nameof(AllowedTargetIds));
        OnPropertyChanged(nameof(HasUnresolvedTargets));
        return true;
    }

    [ObservableProperty]
    private bool _isEnabled;

    partial void OnIsEnabledChanged(bool value)
    {
        if (!_suppressDirtyTracking) _isDirty = true;
    }

    private void OnTargetPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(AgentTargetOptionViewModel.IsSelected)) return;

        if (!_suppressDirtyTracking) _isDirty = true;
        OnPropertyChanged(nameof(AllowedTargetIds));
    }

    private void ClearTargets()
    {
        foreach (var target in Targets)
        {
            target.PropertyChanged -= OnTargetPropertyChanged;
        }

        Targets.Clear();
    }

    private void SetUnresolvedTargets(IEnumerable<string> targetIds)
    {
        _unresolvedTargetIds.Clear();
        _unresolvedTargetOrder.Clear();
        foreach (var targetId in targetIds)
        {
            if (!string.IsNullOrWhiteSpace(targetId) && _unresolvedTargetIds.Add(targetId))
            {
                _unresolvedTargetOrder.Add(targetId);
            }
        }
    }
}

public sealed partial class AgentConnectionSettingsViewModel : ObservableObject, IDisposable
{
    private readonly IAppSettingsStore _settings;
    private readonly IAgentRepository _agents;
    private readonly DataClearService? _clear;
    private readonly IAgentGateway? _gateway;
    private readonly IAgentRelayAdministration? _administration;
    private readonly IAgentRelayRuntime? _runtime;
    private readonly AgentArchiveService? _archive;
    private readonly SemaphoreSlim _operationGate = new(1, 1);
    private bool _disposed;
    private AgentRelaySnapshot _currentSnapshot = AgentRelaySnapshot.Disabled;
    private AgentMaintenanceStatus _maintenanceStatus = AgentMaintenanceStatus.Empty;
    private int _archiveCandidateCount;
    private DateTimeOffset? _oldestArchiveCandidateAt;
    private bool _hasIncompleteArchiveBatch;
    private bool _hasActiveAgentWork;

    public AgentConnectionSettingsViewModel(
        IAppSettingsStore settings,
        IAgentRepository agents,
        DataClearService? clear = null,
        IAgentGateway? gateway = null,
        IAgentRelayAdministration? administration = null,
        IAgentRelayRuntime? runtime = null,
        AgentArchiveService? archive = null)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _agents = agents ?? throw new ArgumentNullException(nameof(agents));
        _clear = clear;
        _gateway = gateway;
        _administration = administration;
        _runtime = runtime;
        _archive = archive;
        if (_runtime is not null)
        {
            _runtime.SnapshotChanged += OnRuntimeSnapshotChanged;
            ApplySnapshot(_runtime.Current);
        }
        Reload();
    }

    public ObservableCollection<AgentConnectionItemViewModel> Connections { get; } = new();
    public ObservableCollection<AgentPendingSourceViewModel> PendingSources { get; } = new();
    public ObservableCollection<AgentApprovedSourceViewModel> ApprovedSources { get; } = new();
    public ObservableCollection<AgentMaintenanceCounterViewModel> MaintenanceCounters { get; } = new();

    public bool IsAdministrationAvailable => _administration is not null;
    public bool IsLegacyMode => _administration is null;
    public bool HasPendingSources => PendingSources.Count > 0;
    public bool HasNoPendingSources => !HasPendingSources;
    public bool HasApprovedSources => ApprovedSources.Count > 0;
    public bool HasNoApprovedSources => !HasApprovedSources;
    public bool HasMaintenanceCounters => MaintenanceCounters.Count > 0;
    public int ArchiveCandidateCount => _archiveCandidateCount;
    public bool HasIncompleteArchiveBatch => _hasIncompleteArchiveBatch;
    public bool HasActiveAgentWork => _hasActiveAgentWork;
    public bool CanArchive => _archive is not null
        && IsAdministrationAvailable
        && !IsBusy
        && HasMaintenanceCounters
        && string.IsNullOrWhiteSpace(MaintenanceStatus.SafeError)
        && !HasActiveAgentWork
        && !MaintenanceCounters.Any(item => item.Counter.Name == "relay_archive_tombstones" && item.Counter.IsFull)
        && (ArchiveCandidateCount > 0 || HasIncompleteArchiveBatch);

    public AgentRelaySnapshot CurrentSnapshot
    {
        get => _currentSnapshot;
        private set => SetProperty(ref _currentSnapshot, value);
    }

    [ObservableProperty]
    private bool _enabled;

    [ObservableProperty]
    private bool _isBusy;

    public bool CanInteract => !IsBusy;

    [ObservableProperty]
    private string _statusText = "配对由适配器发起，FGO Pet 只管理授权与撤销。";

    partial void OnIsBusyChanged(bool value) => OnPropertyChanged(nameof(CanInteract));

    public string StateText => GetStateText(CurrentSnapshot.State);

    public string RelayStatusText => CurrentSnapshot.RelayOnline ? "在线" : "离线";

    public string AppStatusText => CurrentSnapshot.AppOnline ? "在线" : "离线";

    public string AdapterStatusText => CurrentSnapshot.AdapterOnline ? "在线" : "离线";

    public AgentMaintenanceStatus MaintenanceStatus
    {
        get => _maintenanceStatus;
        private set => SetProperty(ref _maintenanceStatus, value);
    }

    public string MaintenanceStatusText
    {
        get
        {
            if (!IsAdministrationAvailable) return "当前连接模式不支持容量维护。";
            if (!string.IsNullOrWhiteSpace(MaintenanceStatus.SafeError))
                return $"容量状态暂不可用（{MaintenanceStatus.SafeError}）";
            if (!HasMaintenanceCounters) return "尚未读取容量状态，请刷新连接状态。";
            var oldest = _oldestArchiveCandidateAt ?? MaintenanceStatus.OldestArchivableAt;
            return oldest is { }
                ? $"容量状态正常 · 最早可归档记录：{oldest.Value.ToLocalTime():yyyy-MM-dd HH:mm}"
                : "容量状态正常 · 暂无可归档记录";
        }
    }

    public string ArchiveCandidateText => HasActiveAgentWork
        ? "当前存在执行中或待核对任务，归档已暂停；请先完成任务或人工核对，不会自动重试派发。"
        : HasIncompleteArchiveBatch
        ? $"存在未完成批次 {MaintenanceStatus.ActiveBatchId ?? "（本地记录）"}，继续时会恢复原批次，不会创建替代批次。"
        : ArchiveCandidateCount > 0
            ? $"已核对 {ArchiveCandidateCount} 条超过 30 天的终止记录。归档前仍需人工确认。"
            : "没有满足“终止、超过 30 天、最终事件已回执”的归档候选。";

    public string ArchiveActionText => HasIncompleteArchiveBatch ? "继续归档" : "归档安全候选";

    public string InstallationGuidanceText =>
        "开启连接后会启动随应用附带的适配器；本页不会修改用户 PATH 或安装 Codex 插件。请先批准配对，再启用来源并填写允许的项目 ID。项目需通过适配器 target add 命令显式登记；Codex 插件安装步骤见 codex-adapter 指南。";

    public void Reload()
    {
        var current = _settings.Load().AgentConnection;
        Enabled = current.Enabled;
        Connections.Clear();
        foreach (var connection in _agents.ListConnections())
        {
            var sourceEnabled = current.SourceEnabled.TryGetValue(connection.SourceType, out var value)
                ? value
                : connection.Enabled;
            Connections.Add(new AgentConnectionItemViewModel(connection, sourceEnabled));
        }

        OnPropertyChanged(nameof(HasPendingSources));
        OnPropertyChanged(nameof(HasNoPendingSources));
        OnPropertyChanged(nameof(HasApprovedSources));
        OnPropertyChanged(nameof(HasNoApprovedSources));
    }

    public Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        return RunOperationAsync("正在刷新连接状态…", async () =>
        {
            if (_administration is null)
            {
                Reload();
                StatusText = "连接设置已刷新。";
                return;
            }

            ApplySnapshot(await _administration.GetSnapshotAsync(cancellationToken).ConfigureAwait(true));
            await RefreshMaintenanceStatusAsync(cancellationToken).ConfigureAwait(true);
            StatusText = BuildSnapshotStatus(CurrentSnapshot);
        }, cancellationToken);
    }

    public Task TestConnectionAsync(CancellationToken cancellationToken = default)
    {
        return RunOperationAsync("正在测试 Relay、App 与适配器连接…", async () =>
        {
            if (_administration is null)
            {
                StatusText = _gateway is null ? "尚未配置连接服务。" : "旧版连接通道未提供分层连接测试。";
                return;
            }

            ApplySnapshot(await _administration.TestConnectionAsync(cancellationToken).ConfigureAwait(true));
            await RefreshMaintenanceStatusAsync(cancellationToken).ConfigureAwait(true);
            StatusText = BuildSnapshotStatus(CurrentSnapshot);
        }, cancellationToken);
    }

    public Task DecideRegistrationAsync(string requestId, bool approve, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(requestId)) throw new ArgumentException("requestId 不能为空。", nameof(requestId));
        if (_administration is null)
        {
            StatusText = "当前连接模式不支持管理待批准来源。";
            return Task.CompletedTask;
        }

        return RunOperationAsync(approve ? "正在批准适配器…" : "正在拒绝适配器…", async () =>
        {
            await _administration.DecideRegistrationAsync(requestId, approve, cancellationToken).ConfigureAwait(true);
            await RefreshSnapshotAsync(cancellationToken).ConfigureAwait(true);
            StatusText = approve ? "适配器已批准，正在等待首次认证。" : "适配器配对请求已拒绝。";
        }, cancellationToken);
    }

    public Task SaveSourceAsync(AgentApprovedSourceViewModel source, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (_administration is null)
        {
            StatusText = "当前连接模式不支持按实例保存权限。";
            return Task.CompletedTask;
        }

        return RunOperationAsync("正在保存来源权限…", async () =>
        {
            await _administration.UpdatePermissionsAsync(
                source.SourceType,
                source.SourceInstanceId,
                source.AllowedTargetIds,
                source.IsEnabled,
                cancellationToken).ConfigureAwait(true);
            await RefreshSnapshotAsync(cancellationToken).ConfigureAwait(true);
            StatusText = $"已保存来源“{source.DisplayName}”的启用状态和项目权限。";
        }, cancellationToken);
    }

    public Task RevokeSourceAsync(AgentApprovedSourceViewModel source, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (_administration is null)
        {
            StatusText = "当前连接模式不支持撤销来源。";
            return Task.CompletedTask;
        }

        return RunOperationAsync("正在撤销来源授权…", async () =>
        {
            await _administration.RevokeSourceAsync(source.SourceType, source.SourceInstanceId, cancellationToken)
                .ConfigureAwait(true);
            await RefreshSnapshotAsync(cancellationToken).ConfigureAwait(true);
            StatusText = $"已撤销来源“{source.DisplayName}”的授权。旧凭据立即失效。";
        }, cancellationToken);
    }

    public async Task SaveAsync(CancellationToken cancellationToken = default)
    {
        await RunOperationAsync("正在保存连接设置…", async () =>
        {
            var current = _settings.Load();
            if (_administration is not null)
            {
                // Per-instance permissions are authoritative in the Relay administration API.
                // Do not mirror the old source-type dictionaries when that API is available.
                await Task.Run(() => _settings.Save(current with
                {
                    AgentConnection = new AgentConnectionSettings(
                        Enabled,
                        current.AgentConnection.SourceEnabled,
                        current.AgentConnection.ProjectAllowlist),
                }), cancellationToken).ConfigureAwait(true);
                if (_runtime is not null)
                {
                    await _runtime.SetEnabledAsync(Enabled, cancellationToken).ConfigureAwait(true);
                }

                StatusText = Enabled ? "Agent 连接已启用。" : "Agent 总开关已关闭；配对和来源权限保留。";
                return;
            }

            var sourceEnabled = Connections.ToDictionary(item => item.SourceType, item => item.IsEnabled, StringComparer.Ordinal);
            var allowlist = current.AgentConnection.ProjectAllowlist.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
            foreach (var item in Connections)
            {
                if (!allowlist.ContainsKey(item.SourceType))
                {
                    allowlist[item.SourceType] = item.Targets;
                }
            }

            await Task.Run(() => _settings.Save(current with
            {
                AgentConnection = new AgentConnectionSettings(Enabled, sourceEnabled, allowlist),
            }), cancellationToken).ConfigureAwait(true);
            if (_gateway is not null)
            {
                await _gateway.SetConnectionEnabledAsync(Enabled, cancellationToken).ConfigureAwait(true);
                foreach (var item in Connections)
                {
                    await _gateway.SetSourceEnabledAsync(item.SourceType, item.IsEnabled, cancellationToken).ConfigureAwait(true);
                    var targets = allowlist.TryGetValue(item.SourceType, out var configured)
                        ? configured.Select(target => target.TargetId).ToArray()
                        : item.Targets.Select(target => target.TargetId).ToArray();
                    await _gateway.SetAllowedTargetsAsync(item.SourceType, targets, cancellationToken).ConfigureAwait(true);
                }
            }

            StatusText = Enabled ? "Agent 连接设置已保存。" : "Agent 总开关已关闭，待发送事件将被清空。";
        }, cancellationToken).ConfigureAwait(true);
    }

    public async Task ClearAgentTodoDataAsync(CancellationToken cancellationToken = default)
    {
        await RunOperationAsync("正在清除 Agent Todo 数据…", async () =>
        {
            if (_clear is not null)
            {
                await _clear.ClearAgentTodoDataAsync(cancellationToken).ConfigureAwait(true);
            }

            StatusText = "Agent Todo、执行投影、事件回执与归档明细已清除；配对和权限保留。";
        }, cancellationToken).ConfigureAwait(true);
    }

    public Task RunArchiveAsync(CancellationToken cancellationToken = default)
    {
        if (_archive is null || _administration is null)
        {
            StatusText = "当前连接模式不支持安全归档。";
            return Task.CompletedTask;
        }

        return RunOperationAsync("正在执行安全归档…", async () =>
        {
            var result = await _archive.RunAsync(cancellationToken).ConfigureAwait(true);
            await RefreshMaintenanceStatusAsync(cancellationToken).ConfigureAwait(true);
            StatusText = result.Result switch
            {
                "completed" => $"已完成安全归档：{result.CandidateCount} 条记录。",
                "no_candidates" => "没有满足条件的安全归档候选。",
                "blocked_active_work" => "当前有执行中或待核对任务，归档已暂停；请先处理这些任务。",
                "unknown" => $"归档批次 {result.BatchId} 的结果未知，请先刷新状态；不会自动再次发起请求。",
                "rejected" => $"安全归档被拒绝：{result.SafeError ?? "未提供原因"}。",
                _ => "安全归档未完成，请刷新状态后再处理。",
            };
        }, cancellationToken);
    }

    public void ReportUiError()
    {
        if (!IsBusy)
        {
            StatusText = "操作失败，请重试；如果问题持续，请先测试连接。";
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_runtime is not null)
        {
            _runtime.SnapshotChanged -= OnRuntimeSnapshotChanged;
        }

    }

    private async Task RefreshSnapshotAsync(CancellationToken cancellationToken)
    {
        if (_administration is not null)
        {
            ApplySnapshot(await _administration.GetSnapshotAsync(cancellationToken).ConfigureAwait(true));
        }
    }

    private async Task RefreshMaintenanceStatusAsync(CancellationToken cancellationToken)
    {
        if (_administration is null) return;

        var status = await _administration.GetMaintenanceStatusAsync(cancellationToken).ConfigureAwait(true);
        MaintenanceStatus = status ?? AgentMaintenanceStatus.Empty;
        MaintenanceCounters.Clear();
        foreach (var counter in MaintenanceStatus.Counters)
        {
            MaintenanceCounters.Add(new AgentMaintenanceCounterViewModel(counter));
        }

        var candidates = _archive?.BuildCandidates();
        _archiveCandidateCount = candidates?.Count ?? 0;
        _oldestArchiveCandidateAt = candidates?.FirstOrDefault()?.EndedAt;
        _hasIncompleteArchiveBatch = _agents.ListIncompleteArchiveBatches().Count > 0;
        _hasActiveAgentWork = _agents.ListNonTerminalExecutions().Count > 0;
        OnPropertyChanged(nameof(HasMaintenanceCounters));
        OnPropertyChanged(nameof(ArchiveCandidateCount));
        OnPropertyChanged(nameof(HasIncompleteArchiveBatch));
        OnPropertyChanged(nameof(HasActiveAgentWork));
        OnPropertyChanged(nameof(CanArchive));
        OnPropertyChanged(nameof(MaintenanceStatusText));
        OnPropertyChanged(nameof(ArchiveCandidateText));
        OnPropertyChanged(nameof(ArchiveActionText));
    }

    private async Task RunOperationAsync(string busyText, Func<Task> operation, CancellationToken cancellationToken)
    {
        if (_disposed) return;
        if (!await _operationGate.WaitAsync(0, cancellationToken).ConfigureAwait(true))
        {
            StatusText = "已有操作正在进行，请稍候。";
            return;
        }

        try
        {
            IsBusy = true;
            OnPropertyChanged(nameof(CanArchive));
            StatusText = busyText;
            await operation().ConfigureAwait(true);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            StatusText = "操作已取消。";
        }
        catch (Exception ex) when (ex is IOException or TimeoutException or InvalidDataException or UnauthorizedAccessException)
        {
            StatusText = "操作失败，请检查 Relay 和适配器是否正在运行，然后重试。";
        }
        finally
        {
            IsBusy = false;
            OnPropertyChanged(nameof(CanArchive));
            _operationGate.Release();
        }
    }

    private void OnRuntimeSnapshotChanged(AgentRelaySnapshot snapshot)
    {
        if (_disposed || snapshot is null) return;
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess())
        {
            ApplySnapshot(snapshot, preserveApprovedEditors: true);
            return;
        }

        _ = dispatcher.BeginInvoke(new Action(() =>
        {
            if (!_disposed) ApplySnapshot(snapshot, preserveApprovedEditors: true);
        }), DispatcherPriority.DataBind);
    }

    private void ApplySnapshot(AgentRelaySnapshot snapshot, bool preserveApprovedEditors = false)
    {
        if (_disposed) return;
        CurrentSnapshot = snapshot ?? AgentRelaySnapshot.Disabled;
        var pendingByRequestId = PendingSources.ToDictionary(pending => pending.RequestId, StringComparer.Ordinal);
        var pendingIds = CurrentSnapshot.PendingSources.Select(pending => pending.RequestId).ToHashSet(StringComparer.Ordinal);
        for (var index = PendingSources.Count - 1; index >= 0; index--)
        {
            if (!pendingIds.Contains(PendingSources[index].RequestId)) PendingSources.RemoveAt(index);
        }
        foreach (var pending in CurrentSnapshot.PendingSources)
        {
            if (!pendingByRequestId.ContainsKey(pending.RequestId))
            {
                PendingSources.Add(new AgentPendingSourceViewModel(pending));
            }
        }

        var existingApproved = ApprovedSources.ToDictionary(source => (source.SourceType, source.SourceInstanceId));
        var approvedKeys = CurrentSnapshot.Sources
            .Select(source => (source.SourceType, source.SourceInstanceId))
            .ToHashSet();
        var keepEditorsWhileUnavailable = preserveApprovedEditors && !CurrentSnapshot.RelayOnline;
        for (var index = ApprovedSources.Count - 1; index >= 0; index--)
        {
            var key = (ApprovedSources[index].SourceType, ApprovedSources[index].SourceInstanceId);
            if (!approvedKeys.Contains(key) && !keepEditorsWhileUnavailable) ApprovedSources.RemoveAt(index);
        }

        if (!preserveApprovedEditors)
        {
            ApprovedSources.Clear();
            existingApproved.Clear();
        }

        foreach (var source in CurrentSnapshot.Sources)
        {
            if (existingApproved.TryGetValue((source.SourceType, source.SourceInstanceId), out var existing))
            {
                existing.UpdateStatus(source);
            }
            else
            {
                ApprovedSources.Add(new AgentApprovedSourceViewModel(source));
            }
        }

        OnPropertyChanged(nameof(StateText));
        OnPropertyChanged(nameof(RelayStatusText));
        OnPropertyChanged(nameof(AppStatusText));
        OnPropertyChanged(nameof(AdapterStatusText));
        OnPropertyChanged(nameof(HasPendingSources));
        OnPropertyChanged(nameof(HasNoPendingSources));
        OnPropertyChanged(nameof(HasApprovedSources));
        OnPropertyChanged(nameof(HasNoApprovedSources));
        if (!IsBusy)
        {
            StatusText = BuildSnapshotStatus(CurrentSnapshot);
        }
    }

    private static string BuildSnapshotStatus(AgentRelaySnapshot snapshot)
    {
        var status = GetStateText(snapshot.State);
        return string.IsNullOrWhiteSpace(snapshot.SafeError) ? status : $"{status}（{snapshot.SafeError}）";
    }

    private static string GetStateText(AgentRelayConnectionState state) => state switch
    {
        AgentRelayConnectionState.Disabled => "已禁用",
        AgentRelayConnectionState.RelayOffline => "Relay 离线",
        AgentRelayConnectionState.AwaitingApproval => "等待批准",
        AgentRelayConnectionState.AdapterOffline => "适配器离线",
        AgentRelayConnectionState.AuthenticationFailed => "认证失败",
        AgentRelayConnectionState.VersionMismatch => "协议版本不匹配",
        AgentRelayConnectionState.Connected => "已连接",
        _ => "未知状态",
    };
}
