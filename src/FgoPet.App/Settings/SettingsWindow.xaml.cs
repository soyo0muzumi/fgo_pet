using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using FgoPet.App.Settings.Views;

namespace FgoPet.App.Settings;

public delegate object? SettingsPageContentResolver(
    SettingsSection section,
    PackageDetailRoute? packageDetail);

public partial class SettingsWindow : Window
{
    private readonly SettingsViewModel _viewModel;
    private readonly SettingsPageContentResolver _resolvePageContent;
    private bool _refreshing;


    public SettingsWindow(
        SettingsViewModel viewModel,
        SettingsPageContentResolver resolvePageContent)
    {
        _viewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
        _resolvePageContent = resolvePageContent ?? throw new ArgumentNullException(nameof(resolvePageContent));
        InitializeComponent();
        DataContext = viewModel;
        SettingsNavigation.ItemsSource = Categories;
        SettingsNavigation.SelectionChanged += SettingsNavigation_SelectionChanged;
        _viewModel.PropertyChanged += OnViewModelPropertyChanged;
        RefreshRoute();
        Closing += OnClosing;
    }

    internal ListBox SettingsNavigation => SettingsShell.SettingsNavigation;
    internal ContentControl SettingsContent => SettingsShell.ContentHost;
    internal FrameworkElement PackageBreadcrumb => SettingsShell.Breadcrumb;
    internal TextBlock PackageBreadcrumbText => SettingsShell.BreadcrumbText;
    internal TextBlock PageTitleText => SettingsShell.PageTitle;
    internal TextBlock PageDescriptionText => SettingsShell.PageDescription;
    private void SettingsNavigation_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_refreshing && SettingsNavigation.SelectedValue is SettingsSection section &&
            section != _viewModel.SelectedSection)
        {
            _viewModel.Select(section);
        }
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(SettingsViewModel.SelectedSection) or nameof(SettingsViewModel.PackageDetail))
        {
            RefreshRoute();
        }
    }

    private static readonly SettingsNavigationItem[] Categories =
    [
        new(SettingsSection.Personalization, "外观与角色", "选择陪伴你的角色，调整桌宠的显示方式。", "Appearance"),
        new(SettingsSection.ModelConnection, "模型服务", "连接聊天服务，选择默认使用的模型。", "Model"),
        new(SettingsSection.Speech, "语音朗读", "选择角色的声音，调整朗读与播放偏好。", "Speech"),
        new(SettingsSection.AgentConnection, "Agent 连接", "连接你的 Agent，管理可访问的项目。", "Agent"),
        new(SettingsSection.Privacy, "通用与数据", "管理个人偏好、记忆与本地数据。", "Data"),
    ];

    private void RefreshRoute()
    {
        var category = _viewModel.SelectedSection switch
        {
            SettingsSection.RolePackages or SettingsSection.Theme => SettingsSection.Personalization,
            SettingsSection.UserProfile or SettingsSection.ConversationMemory => SettingsSection.Privacy,
            var section => section,
        };
        var item = Categories.First(x => x.Section == category);
        _refreshing = true;
        try { SettingsNavigation.SelectedValue = category; }
        finally { _refreshing = false; }
        PackageBreadcrumb.Visibility = Visibility.Collapsed;
        var route = category == SettingsSection.Personalization && _viewModel.PackageDetail is not null
            ? _viewModel.PackageDetail
            : null;
        var isRolePackages = _viewModel.SelectedSection == SettingsSection.RolePackages && route is null;
        PageTitleText.Text = isRolePackages ? "角色包" : item.Label;
        PageDescriptionText.Text = isRolePackages ? "浏览、安装并切换已安装的角色包。" : item.Description;
        var contentSection = _viewModel.SelectedSection == SettingsSection.RolePackages || route is not null
            ? SettingsSection.RolePackages
            : category;
        SettingsContent.Content = _resolvePageContent(contentSection, route);
        PackageBreadcrumb.Visibility = route is null ? Visibility.Collapsed : Visibility.Visible;
        PackageBreadcrumbText.Text = route is null ? string.Empty : $"角色包 / {route.DisplayName}";
    }

    private static SettingsPreviewRow[] GetPreviewRows(SettingsSection section) => section switch
    {
        SettingsSection.Personalization =>
        [
            new("当前角色", "角色与外观 · 切换角色 · 角色称呼"),
            new("桌宠显示", "立绘大小 · 始终置顶 · 无操作时自动收起"),
            new("主题", "应用的颜色与外观"),
            new("角色包管理", "安装本地角色包 · 已安装角色包 · 角色专属设置"),
        ],
        SettingsSection.ModelConnection =>
        [
            new("服务连接", "服务商 · 接口地址 · 密钥"),
            new("默认聊天模型", "可用模型列表 · 刷新模型 · 手动填写模型"),
            new("连接验证", "测试连接 · 连接结果 · 保存配置"),
        ],
        SettingsSection.Speech =>
        [
            new("朗读服务", "启用朗读 · 选择服务 · 配置所选服务"),
            new("声音与播放", "音色 · 语速 · 音量 · 试听"),
            new("自动朗读", "新回复自动朗读 · 免打扰 · 朗读字数上限"),
        ],
        SettingsSection.AgentConnection =>
        [
            new("连接状态", "当前 Agent · 连接状态 · 接入引导"),
            new("配对与项目授权", "确认连接来源 · 选择允许访问的项目 · 保存权限"),
            new("连接管理", "测试连接 · 重新配对 · 撤销授权"),
            new("高级诊断与维护", "诊断信息 · 容量与归档"),
        ],
        _ =>
        [
            new("个人与对话偏好", "用户显示名称 · 回复显示偏好"),
            new("记忆管理", "记忆开关 · 待确认记忆 · 已确认记忆"),
            new("数据管理", "会话管理 · 数据导出 · 私有备份与恢复"),
            new("数据清除", "集中管理删除操作，执行前确认范围"),
        ],
    };
    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (Dispatcher.HasShutdownStarted) return;
        e.Cancel = true;
        Hide();
    }

}

public sealed record SettingsPreviewRow(string Title, string Description);
