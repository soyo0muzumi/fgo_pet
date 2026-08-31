using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FgoPet.App.Services;
using FgoPet.Core.Agents;
using FgoPet.Core.Todo;

namespace FgoPet.App.ViewModels;

/// <summary>
/// View model for the explicit Todo -> Agent dispatch confirmation flow.
///
/// The relay administration snapshot is the source of truth here. In particular,
/// this view model never consults the legacy persisted agent-connection grants: a
/// source instance and its opaque target IDs must be selected from the current
/// authorized relay snapshot immediately before dispatch.
/// </summary>
public sealed partial class AgentDispatchDialogViewModel : ObservableObject, IDisposable
{
    private readonly IAgentRelayAdministration _administration;
    private readonly AgentDispatchService _dispatchService;
    private readonly CancellationTokenSource _lifetime = new();
    private bool _loaded;
    private bool _disposed;

    public AgentDispatchDialogViewModel(
        TodoItem todo,
        IAgentRelayAdministration administration,
        AgentDispatchService dispatchService)
    {
        Todo = todo ?? throw new ArgumentNullException(nameof(todo));
        _administration = administration ?? throw new ArgumentNullException(nameof(administration));
        _dispatchService = dispatchService ?? throw new ArgumentNullException(nameof(dispatchService));

        ConfirmCommand = new AsyncRelayCommand(ConfirmAsync, () => CanConfirm);
    }

    public TodoItem Todo { get; }

    /// <summary>Enabled and online sources in the latest authoritative snapshot.</summary>
    public ObservableCollection<AgentApprovedSource> Sources { get; } = new();

    /// <summary>Opaque target IDs supplied by the selected adapter source.</summary>
    public ObservableCollection<string> Targets { get; } = new();

    public IReadOnlyList<AgentApprovedSource> AvailableSources => Sources;

    public IAsyncRelayCommand ConfirmCommand { get; }

    public AgentDispatchResult? LastResult { get; private set; }

    public event Action<AgentDispatchResult>? DispatchCompleted;

    [ObservableProperty]
    private AgentApprovedSource? _selectedSource;

    [ObservableProperty]
    private string? _selectedTarget;

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private bool _isLoaded;

    [ObservableProperty]
    private string _statusText = "正在读取可用来源…";

    [ObservableProperty]
    private string _errorText = string.Empty;

    public bool CanConfirm =>
        !IsBusy
        && IsLoaded
        && SelectedSource is { Enabled: true, IsOnline: true } source
        && !string.IsNullOrWhiteSpace(SelectedTarget)
        && (source.AllowedTargetIds?.Contains(SelectedTarget, StringComparer.Ordinal) ?? false);

    public bool CanSelect => IsLoaded && !IsBusy;

    public string SelectedSourceInstanceId => SelectedSource?.SourceInstanceId ?? string.Empty;

    /// <summary>
    /// Loads the current relay administration snapshot once. This is deliberately
    /// separate from the constructor so opening the modal never blocks the WPF UI
    /// thread and no dispatch can occur as a side effect of loading.
    /// </summary>
    public async Task LoadAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_loaded)
        {
            return;
        }

        IsBusy = true;
        ErrorText = string.Empty;
        StatusText = "正在读取可用来源…";
        try
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
            var snapshot = await _administration.GetSnapshotAsync(linked.Token).ConfigureAwait(true);
            linked.Token.ThrowIfCancellationRequested();
            Sources.Clear();
            foreach (var source in snapshot.Sources
                .Where(source => source.Enabled && source.IsOnline)
                .OrderBy(source => source.DisplayName, StringComparer.CurrentCulture)
                .ThenBy(source => source.SourceType, StringComparer.Ordinal)
                .ThenBy(source => source.SourceInstanceId, StringComparer.Ordinal))
            {
                Sources.Add(source);
            }

            SelectedSource = Sources.FirstOrDefault();
            IsLoaded = true;
            StatusText = Sources.Count == 0
                ? "没有可用的在线授权来源。请在 Agent 设置中启用来源并授予项目权限。"
                : "请选择来源和目标后确认派发。";
            if (!snapshot.RelayOnline && Sources.Count == 0)
            {
                ErrorText = snapshot.SafeError ?? "Relay 当前不可用。";
            }
            _loaded = true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested || _lifetime.IsCancellationRequested)
        {
            // Closing the modal is an expected cancellation path, not an error.
        }
        catch (Exception)
        {
            IsLoaded = false;
            StatusText = "无法读取可用来源。";
            ErrorText = "Relay 当前不可用，请稍后重试或检查 Agent 设置。";
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>Signals that the hosting Window is closing and cancels any I/O.</summary>
    public void Cancel()
    {
        if (!_lifetime.IsCancellationRequested)
        {
            _lifetime.Cancel();
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Cancel();
        _lifetime.Dispose();
    }

    partial void OnSelectedSourceChanged(AgentApprovedSource? value)
    {
        RefreshTargets();
        OnPropertyChanged(nameof(SelectedSourceInstanceId));
        OnPropertyChanged(nameof(CanConfirm));
        ConfirmCommand.NotifyCanExecuteChanged();
    }

    partial void OnSelectedTargetChanged(string? value)
    {
        OnPropertyChanged(nameof(CanConfirm));
        ConfirmCommand.NotifyCanExecuteChanged();
    }

    partial void OnIsBusyChanged(bool value)
    {
        OnPropertyChanged(nameof(CanConfirm));
        OnPropertyChanged(nameof(CanSelect));
        ConfirmCommand.NotifyCanExecuteChanged();
    }

    partial void OnIsLoadedChanged(bool value)
    {
        OnPropertyChanged(nameof(CanConfirm));
        OnPropertyChanged(nameof(CanSelect));
        ConfirmCommand.NotifyCanExecuteChanged();
    }

    private async Task ConfirmAsync()
    {
        if (_disposed || !CanConfirm || SelectedSource is not { } source || SelectedTarget is not { } target)
        {
            return;
        }

        IsBusy = true;
        ErrorText = string.Empty;
        StatusText = "正在派发…";
        try
        {
            // This is the only send path. It is reached only by the explicit
            // confirmation command; loading a snapshot never dispatches.
            var result = await _dispatchService.DispatchAsync(
                Todo,
                source.SourceType,
                target,
                confirmed: true,
                cancellationToken: _lifetime.Token,
                sourceInstanceId: source.SourceInstanceId).ConfigureAwait(true);
            if (_lifetime.IsCancellationRequested)
            {
                return;
            }

            LastResult = result;
            OnPropertyChanged(nameof(LastResult));
            if (result.Status is AgentDispatchStatus.Accepted or AgentDispatchStatus.AlreadyApplied)
            {
                StatusText = $"已派发到 {source.DisplayName} · {target}";
            }
            else
            {
                StatusText = "派发未完成。";
                ErrorText = FormatDispatchError(result.SafeError);
            }

            DispatchCompleted?.Invoke(result);
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
            // Closing the Window cancels the request; do not show a stale error.
        }
        catch (Exception)
        {
            StatusText = "派发未完成。";
            ErrorText = "无法连接到 Relay，请检查 Agent 设置后重试。";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void RefreshTargets()
    {
        Targets.Clear();
        if (SelectedSource is not { } source)
        {
            SelectedTarget = null;
            return;
        }

        foreach (var target in (source.AllowedTargetIds ?? Array.Empty<string>())
            .Where(target => !string.IsNullOrWhiteSpace(target))
            .Distinct(StringComparer.Ordinal))
        {
            Targets.Add(target);
        }

        SelectedTarget = Targets.FirstOrDefault();
    }

    private static string FormatDispatchError(string? safeError) => safeError switch
    {
        "relay_offline" or "relay_timeout" => "Relay 当前不可用，请稍后重试。",
        "adapter_offline" => "该来源当前离线，请重新连接适配器后重试。",
        "target_not_authorized" => "该目标已不再授权，请刷新 Agent 设置。",
        "source_instance_required" => "来源实例发生变化，请关闭后重新打开派发窗口。",
        "todo_not_dispatchable" => "该 Todo 已不再处于可派发状态。",
        "confirmation_required" => "请使用确认派发按钮发送。",
        _ => "Relay 拒绝了这次派发，请刷新来源和目标后重试。",
    };
}
