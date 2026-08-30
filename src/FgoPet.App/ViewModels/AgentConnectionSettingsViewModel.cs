using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using FgoPet.Core.Agents;
using FgoPet.Core.Settings;
using FgoPet.App.Services;

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

public sealed partial class AgentConnectionSettingsViewModel : ObservableObject
{
    private readonly IAppSettingsStore _settings;
    private readonly IAgentRepository _agents;
    private readonly DataClearService? _clear;
    private readonly IAgentGateway? _gateway;

    public AgentConnectionSettingsViewModel(
        IAppSettingsStore settings,
        IAgentRepository agents,
        DataClearService? clear = null,
        IAgentGateway? gateway = null)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _agents = agents ?? throw new ArgumentNullException(nameof(agents));
        _clear = clear;
        _gateway = gateway;
        Reload();
    }

    public ObservableCollection<AgentConnectionItemViewModel> Connections { get; } = new();

    [ObservableProperty]
    private bool _enabled;

    [ObservableProperty]
    private string _statusText = "配对由适配器发起，FGO Pet 只管理授权与撤销。";

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
    }

    public void Save()
    {
        var current = _settings.Load();
        var sourceEnabled = Connections.ToDictionary(item => item.SourceType, item => item.IsEnabled, StringComparer.Ordinal);
        var allowlist = current.AgentConnection.ProjectAllowlist.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
        foreach (var item in Connections)
        {
            if (!allowlist.ContainsKey(item.SourceType))
            {
                allowlist[item.SourceType] = item.Targets;
            }
        }

        _settings.Save(current with
        {
            AgentConnection = new AgentConnectionSettings(Enabled, sourceEnabled, allowlist),
        });
        _gateway?.SetConnectionEnabledAsync(Enabled).GetAwaiter().GetResult();
        StatusText = Enabled ? "Agent 连接设置已保存。" : "Agent 总开关已关闭，待发送事件将被清空。";
    }

    public void ClearAgentTodoData()
    {
        _clear?.ClearAgentTodoData();
        StatusText = "Agent Todo、执行投影、事件回执与归档明细已清除；配对和 allowlist 保留。";
    }
}
