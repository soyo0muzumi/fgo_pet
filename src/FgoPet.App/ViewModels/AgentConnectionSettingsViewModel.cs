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

public sealed partial class AgentApprovedSourceViewModel : ObservableObject
{
    private AgentApprovedSource _source;
    private bool _isDirty;
    private bool _suppressDirtyTracking;

    public AgentApprovedSourceViewModel(AgentApprovedSource source)
    {
        _source = source ?? throw new ArgumentNullException(nameof(source));
        _isEnabled = source.Enabled;
        _targetIdsText = string.Join(Environment.NewLine, source.AllowedTargetIds);
    }

    public AgentApprovedSource Source => _source;
    public string SourceType => _source.SourceType;
    public string SourceInstanceId => _source.SourceInstanceId;
    public string DisplayName => _source.DisplayName;
    public string AdapterVersion => _source.AdapterVersion;
    public IReadOnlyList<string> AllowedTargetIds => ParseTargetIds(TargetIdsText);
    public string OnlineText => _source.IsOnline ? "在线" : "离线";

    public void UpdateStatus(AgentApprovedSource source)
    {
        _source = source ?? throw new ArgumentNullException(nameof(source));
        if (!_isDirty)
        {
            _suppressDirtyTracking = true;
            IsEnabled = source.Enabled;
            TargetIdsText = string.Join(Environment.NewLine, source.AllowedTargetIds);
            _suppressDirtyTracking = false;
        }

        OnPropertyChanged(nameof(Source));
        OnPropertyChanged(nameof(SourceType));
        OnPropertyChanged(nameof(SourceInstanceId));
        OnPropertyChanged(nameof(DisplayName));
        OnPropertyChanged(nameof(AdapterVersion));
        OnPropertyChanged(nameof(AllowedTargetIds));
        OnPropertyChanged(nameof(OnlineText));
    }

    [ObservableProperty]
    private bool _isEnabled;

    [ObservableProperty]
    private string _targetIdsText;

    partial void OnIsEnabledChanged(bool value)
    {
        if (!_suppressDirtyTracking) _isDirty = true;
    }

    partial void OnTargetIdsTextChanged(string value)
    {
        if (!_suppressDirtyTracking) _isDirty = true;
    }

    private static IReadOnlyList<string> ParseTargetIds(string? value)
    {
        return (value ?? string.Empty)
            .Split(new[] { '\r', '\n', ',', ';' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(id => id.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
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
    private readonly SemaphoreSlim _operationGate = new(1, 1);
    private bool _disposed;
    private AgentRelaySnapshot _currentSnapshot = AgentRelaySnapshot.Disabled;

    public AgentConnectionSettingsViewModel(
        IAppSettingsStore settings,
        IAgentRepository agents,
        DataClearService? clear = null,
        IAgentGateway? gateway = null,
        IAgentRelayAdministration? administration = null,
        IAgentRelayRuntime? runtime = null)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _agents = agents ?? throw new ArgumentNullException(nameof(agents));
        _clear = clear;
        _gateway = gateway;
        _administration = administration;
        _runtime = runtime;
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

    public bool IsAdministrationAvailable => _administration is not null;
    public bool IsLegacyMode => _administration is null;
    public bool HasPendingSources => PendingSources.Count > 0;
    public bool HasNoPendingSources => !HasPendingSources;
    public bool HasApprovedSources => ApprovedSources.Count > 0;
    public bool HasNoApprovedSources => !HasApprovedSources;

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
