using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
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
    private SettingsSection _lastCapability = SettingsSection.ModelConnection;
    private readonly Dictionary<(SettingsSection Section, string? Package), double> _scrollOffsets = [];
    private (SettingsSection Section, string? Package)? _currentPage;
    internal event Action? ChatRequested;
    internal event Action? TodoRequested;
    internal event Action? FocusRequested;

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
        SettingsNavigation.AddHandler(
            Mouse.PreviewMouseUpEvent,
            new MouseButtonEventHandler(SettingsNavigation_PreviewMouseLeftButtonUp),
            true);
        SettingsNavigation.PreviewKeyDown += SettingsNavigation_PreviewKeyDown;
        SettingsShell.CapabilityTabs.ItemsSource = Capabilities;
        System.Windows.Automation.AutomationProperties.SetName(SettingsShell.CapabilityTabs, "能力页面");
        SettingsShell.CapabilityTabs.SelectionChanged += (_, _) =>
        {
            if (!_refreshing && SettingsShell.CapabilityTabs.SelectedValue is SettingsSection section)
                _viewModel.Select(section);
        };
        SettingsShell.ChatButton.Click += (_, _) => ChatRequested?.Invoke();
        SettingsShell.TodoButton.Click += (_, _) => TodoRequested?.Invoke();
        SettingsShell.FocusButton.Click += (_, _) => FocusRequested?.Invoke();
        _viewModel.PropertyChanged += OnViewModelPropertyChanged;
        RefreshRoute();
        Closing += OnClosing;
        PreviewMouseWheel += OnSettingsMouseWheel;
    }

    internal void SetNavigationAvailability(bool dialogueAvailable, bool focusAvailable)
    {
        SettingsShell.ChatButton.IsEnabled = SettingsShell.TodoButton.IsEnabled = dialogueAvailable;
        SettingsShell.FocusButton.IsEnabled = focusAvailable;
    }

    private void OnSettingsMouseWheel(object sender, System.Windows.Input.MouseWheelEventArgs e)
    {
        var node = e.OriginalSource as DependencyObject;
        ComboBox? combo = null;
        while (node is not null)
        {
            if (node is ComboBox candidate) { combo = candidate; break; }
            node = node is System.Windows.Media.Visual or System.Windows.Media.Media3D.Visual3D
                ? System.Windows.Media.VisualTreeHelper.GetParent(node) : LogicalTreeHelper.GetParent(node);
        }
        if (combo is null || combo.IsDropDownOpen) return;
        node = System.Windows.Media.VisualTreeHelper.GetParent(combo);
        while (node is not null && node is not ScrollViewer)
            node = System.Windows.Media.VisualTreeHelper.GetParent(node);
        if (node is ScrollViewer scroller)
        {
            e.Handled = true;
            scroller.ScrollToVerticalOffset(scroller.VerticalOffset - e.Delta / 120.0 * SystemParameters.WheelScrollLines * 16);
        }
    }
    internal ListBox SettingsNavigation => SettingsShell.SettingsNavigation;
    internal ContentControl SettingsContent => SettingsShell.ContentHost;
    internal FrameworkElement PackageBreadcrumb => SettingsShell.Breadcrumb;
    internal TextBlock PackageBreadcrumbText => SettingsShell.BreadcrumbText;
    internal TextBlock PageTitleText => SettingsShell.PageTitle;
    internal TextBlock PageDescriptionText => SettingsShell.PageDescription;
    private void SettingsNavigation_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_refreshing && SettingsNavigation.SelectedValue is SettingsSection section)
        {
            ActivateCategory(section);
        }
    }

    private void SettingsNavigation_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Left &&
            ItemsControl.ContainerFromElement(SettingsNavigation, e.OriginalSource as DependencyObject)
            is ListBoxItem { DataContext: SettingsNavigationItem item })
        {
            ActivateCategory(item.Section);
        }
    }

    private void SettingsNavigation_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key is not (Key.Enter or Key.Space) ||
            SettingsNavigation.SelectedValue is not SettingsSection section)
        {
            return;
        }

        ActivateCategory(section);
        e.Handled = true;
    }

    private void ActivateCategory(SettingsSection section)
    {
        if (section == SettingsSection.Personalization)
        {
            _viewModel.BackToAppearanceCommand.Execute(null);
            return;
        }

        _viewModel.Select(section == SettingsSection.ModelConnection ? _lastCapability : section);
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
        new(SettingsSection.ModelConnection, "能力", "连接聊天、语音和 Agent 服务。", "Model"),
        new(SettingsSection.Privacy, "通用与数据", "管理个人偏好、记忆与本地数据。", "Data"),
    ];

    private static readonly SettingsNavigationItem[] Capabilities =
    [
        new(SettingsSection.ModelConnection, "模型服务", "连接聊天服务，选择默认使用的模型。", "Model"),
        new(SettingsSection.Speech, "语音朗读", "选择角色的声音，调整朗读与播放偏好。", "Speech"),
        new(SettingsSection.AgentConnection, "Agent 连接", "连接你的 Agent，管理可访问的项目。", "Agent"),
    ];

    private void RefreshRoute()
    {
        var category = _viewModel.SelectedSection switch
        {
            SettingsSection.RolePackages or SettingsSection.Theme => SettingsSection.Personalization,
            SettingsSection.UserProfile or SettingsSection.ConversationMemory => SettingsSection.Privacy,
            SettingsSection.Speech or SettingsSection.AgentConnection => SettingsSection.ModelConnection,
            var section => section,
        };
        var isCapability = category == SettingsSection.ModelConnection;
        if (isCapability) _lastCapability = _viewModel.SelectedSection;
        var item = (isCapability ? Capabilities : Categories).First(x => x.Section == (isCapability ? _lastCapability : category));
        _refreshing = true;
        try
        {
            SettingsNavigation.SelectedValue = category;
            SettingsShell.CapabilityTabs.SelectedValue = _lastCapability;
        }
        finally { _refreshing = false; }
        SettingsShell.CapabilityBar.Visibility = isCapability ? Visibility.Visible : Visibility.Collapsed;
        PackageBreadcrumb.Visibility = Visibility.Collapsed;
        var route = category == SettingsSection.Personalization && _viewModel.PackageDetail is not null
            ? _viewModel.PackageDetail
            : null;
        var isRolePackages = _viewModel.SelectedSection == SettingsSection.RolePackages && route is null;
        PageTitleText.Text = isRolePackages ? "角色包" : item.Label;
        PageDescriptionText.Text = isRolePackages ? "浏览、安装并切换已安装的角色包。" : item.Description;
        var contentSection = _viewModel.SelectedSection == SettingsSection.RolePackages || route is not null
            ? SettingsSection.RolePackages
            : isCapability ? _viewModel.SelectedSection : category;
        var pageKey = (contentSection, route?.PackageId);
        if (_currentPage != pageKey)
        {
            // Apply queued scrolling before recording the outgoing page. This also
            // handles consecutive route changes within the same dispatcher turn.
            SettingsShell.Scroller.UpdateLayout();
            if (_currentPage is { } previous)
                _scrollOffsets[previous] = SettingsShell.Scroller.VerticalOffset;
            _currentPage = pageKey;
            SettingsContent.Content = _resolvePageContent(contentSection, route);
            var ownsScroll = SettingsContent.Content is ModelConnectionPage;
            SettingsShell.Scroller.VerticalScrollBarVisibility = ownsScroll ? ScrollBarVisibility.Disabled : ScrollBarVisibility.Auto;
            SettingsContent.VerticalContentAlignment = ownsScroll ? VerticalAlignment.Stretch : VerticalAlignment.Top;
            SettingsShell.PageHeadingPanel.Visibility = ownsScroll ? Visibility.Collapsed : Visibility.Visible;
            var offset = _scrollOffsets.GetValueOrDefault(pageKey);
            SettingsShell.UpdateLayout();
            SettingsShell.Scroller.ScrollToVerticalOffset(offset);
            SettingsShell.Scroller.UpdateLayout();
        }
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
