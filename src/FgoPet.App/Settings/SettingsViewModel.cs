using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace FgoPet.App.Settings;

public sealed class SettingsViewModel : ObservableObject
{
    private static readonly IReadOnlyList<SettingsNavigationItem> Items =
    [
        new(SettingsSection.UserProfile, "用户资料", "管理全局用户资料。", "IconProfileGeometry"),
        new(SettingsSection.Personalization, "个性化", "调整应用的个性化偏好。", "IconPersonalizationGeometry"),
        new(SettingsSection.RolePackages, "角色包", "安装、浏览并管理角色包。", "IconRolePackageGeometry"),
        new(SettingsSection.ModelConnection, "AI 模型与连接", "配置提供商、凭据、端点和模型。", "IconConnectionGeometry"),
        new(SettingsSection.AgentConnection, "Agent 连接", "管理 Agent 来源、授权和项目 allowlist。", "IconAgentGeometry"),
        new(SettingsSection.ConversationMemory, "对话与记忆", "管理对话和记忆偏好。", "IconConversationGeometry"),
        new(SettingsSection.Privacy, "数据与隐私", "导出或清理本地用户数据。", "IconPrivacyGeometry"),
        new(SettingsSection.Theme, "主题", "选择设置界面的视觉主题。", "IconThemeGeometry"),
    ];

    private SettingsSection _selectedSection;
    private PackageDetailRoute? _packageDetail;

    public SettingsViewModel(SettingsSection selectedSection = SettingsSection.UserProfile)
    {
        if (!Enum.IsDefined(selectedSection))
        {
            throw new ArgumentOutOfRangeException(nameof(selectedSection));
        }

        _selectedSection = selectedSection;
        OpenPackageCommand = new RelayCommand<PackageDetailRoute>(OpenPackage);
        BackToPackagesCommand = new RelayCommand(BackToPackages);
    }

    public IReadOnlyList<SettingsNavigationItem> NavigationItems => Items;

    public SettingsSection SelectedSection
    {
        get => _selectedSection;
        private set
        {
            if (!SetProperty(ref _selectedSection, value)) return;
            OnPropertyChanged(nameof(PageTitle));
            OnPropertyChanged(nameof(PageDescription));
            OnPropertyChanged(nameof(Breadcrumb));
        }
    }

    public PackageDetailRoute? PackageDetail
    {
        get => _packageDetail;
        private set
        {
            if (!SetProperty(ref _packageDetail, value)) return;
            OnPropertyChanged(nameof(PageTitle));
            OnPropertyChanged(nameof(PageDescription));
            OnPropertyChanged(nameof(Breadcrumb));
        }
    }

    public string PageTitle => IsPackageDetailVisible
        ? PackageDetail!.DisplayName
        : CurrentNavigationItem.Label;

    public string PageDescription => IsPackageDetailVisible
        ? "管理角色包外观、称呼和版本信息。"
        : CurrentNavigationItem.Description;

    public string? Breadcrumb => IsPackageDetailVisible
        ? $"设置 / 角色包 / {PackageDetail!.DisplayName}"
        : null;

    public IRelayCommand<PackageDetailRoute> OpenPackageCommand { get; }

    public IRelayCommand BackToPackagesCommand { get; }

    public void Select(SettingsSection section)
    {
        if (!Enum.IsDefined(section))
        {
            throw new ArgumentOutOfRangeException(nameof(section));
        }

        SelectedSection = section;
    }

    private bool IsPackageDetailVisible =>
        SelectedSection == SettingsSection.RolePackages && PackageDetail is not null;

    private SettingsNavigationItem CurrentNavigationItem =>
        Items.First(item => item.Section == SelectedSection);

    private void OpenPackage(PackageDetailRoute? route)
    {
        if (route is null) return;
        PackageDetail = route;
        Select(SettingsSection.RolePackages);
    }

    private void BackToPackages() => PackageDetail = null;
}
