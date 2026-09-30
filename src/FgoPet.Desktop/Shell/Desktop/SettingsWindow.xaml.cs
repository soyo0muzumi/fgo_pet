using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Data;
using FgoPet.Extensibility;
using FgoPet.UiSdk;

namespace FgoPet.App.Settings;

public delegate object? SettingsPageContentResolver(
    SettingsSection section,
    PackageDetailRoute? packageDetail);

public partial class SettingsWindow : Window
{
    private readonly SettingsViewModel _viewModel;
    private readonly SettingsPageContentResolver _resolvePageContent;
    private readonly SettingsPageCatalog? _settingsCatalog;
    private readonly SettingsNavigationItem[] _capabilities;
    private bool _refreshing;
    private string _lastCapabilityPageId = nameof(SettingsSection.ModelConnection);
    private readonly Dictionary<(string PageId, string? Package), double> _scrollOffsets = [];
    private (string PageId, string? Package)? _currentPage;
    internal event Action? ChatRequested;
    internal event Action? TodoRequested;
    internal event Action? FocusRequested;

    public SettingsWindow(SettingsViewModel viewModel, SettingsPageCatalog catalog)
        : this(viewModel, (section, route) => catalog.CreateView(section.ToString(),
            new(route?.PackageId, route?.DisplayName)), catalog.Contains, catalog) { }

    public SettingsWindow(SettingsViewModel viewModel, SettingsPageContentResolver resolvePageContent,
        Func<string, bool>? pageAvailable = null)
        : this(viewModel, resolvePageContent, pageAvailable, null) { }

    private SettingsWindow(SettingsViewModel viewModel, SettingsPageContentResolver resolvePageContent,
        Func<string, bool>? pageAvailable, SettingsPageCatalog? catalog)
    {
        _viewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
        _resolvePageContent = resolvePageContent ?? throw new ArgumentNullException(nameof(resolvePageContent));
        _settingsCatalog = catalog;
        var legacyPages = Capabilities.Where(item => pageAvailable?.Invoke(item.PageId) ?? true)
            .Select(item => catalog?.Pages.FirstOrDefault(page => page.Id == item.PageId) is { } descriptor
                && descriptor.Title != descriptor.Id
                    ? item with { Label = descriptor.Title, Description = descriptor.Description }
                    : item);
        var extensionPages = catalog?.Pages.Where(page => !Enum.GetNames<SettingsSection>().Contains(page.Id, StringComparer.Ordinal))
            .Select(page => new SettingsNavigationItem(SettingsSection.ModelConnection, page.Title, page.Description, "Model")
                { PageId = page.Id }) ?? [];
        _capabilities = [.. legacyPages, .. extensionPages];
        InitializeComponent();
        DataContext = viewModel;
        if (catalog is not null)
        {
            SettingsShell.NavigationColumn.Width = new GridLength(190);
            SettingsShell.LegacyNavigationRail.Visibility = Visibility.Collapsed;
            SettingsShell.MetadataPageNavigation.Visibility = Visibility.Visible;
            SettingsShell.LegacyTopNavigation.Visibility = Visibility.Collapsed;
            SettingsShell.MetadataPageSearch.TextChanged += (_, _) => RefreshMetadataPages();
            SettingsShell.MetadataPageList.SelectionChanged += (_, _) =>
            {
                if (_refreshing) return;
                if (SettingsShell.MetadataPageList.SelectedValue is string pageId)
                    _viewModel.Navigate(pageId);
            };
            RefreshMetadataPages();
        }
        SettingsNavigation.ItemsSource = Categories;
        SettingsNavigation.SelectionChanged += SettingsNavigation_SelectionChanged;
        SettingsNavigation.AddHandler(
            Mouse.PreviewMouseUpEvent,
            new MouseButtonEventHandler(SettingsNavigation_PreviewMouseLeftButtonUp),
            true);
        SettingsNavigation.PreviewKeyDown += SettingsNavigation_PreviewKeyDown;
        SettingsShell.CapabilityTabs.ItemsSource = _capabilities;
        if (catalog is not null) SettingsShell.CapabilityTabs.SelectedValuePath = nameof(SettingsNavigationItem.PageId);
        System.Windows.Automation.AutomationProperties.SetName(SettingsShell.CapabilityTabs, "能力页面");
        SettingsShell.CapabilityTabs.SelectionChanged += (_, _) =>
        {
            if (_refreshing) return;
            if (SettingsShell.CapabilityTabs.SelectedValue is string pageId)
                _viewModel.Navigate(pageId);
            else if (SettingsShell.CapabilityTabs.SelectedValue is SettingsSection section)
                _viewModel.Select(section);
        };
        SettingsShell.ChatButton.Click += (_, _) => ChatRequested?.Invoke();
        SettingsShell.TodoButton.Click += (_, _) => TodoRequested?.Invoke();
        SettingsShell.FocusButton.Click += (_, _) => FocusRequested?.Invoke();
        _viewModel.PropertyChanged += OnViewModelPropertyChanged;
        RefreshRoute();
        Closing += OnClosing;
        IsVisibleChanged += (_, _) =>
        {
            if (IsVisible && _currentPage is null) RefreshRoute();
        };
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

        if (section == SettingsSection.ModelConnection) _viewModel.Navigate(_lastCapabilityPageId);
        else _viewModel.Select(section);
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(SettingsViewModel.SelectedSection) or nameof(SettingsViewModel.SelectedPageId) or nameof(SettingsViewModel.PackageDetail))
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

    private void RefreshMetadataPages()
    {
        if (_settingsCatalog is null) return;
        var view = CollectionViewSource.GetDefaultView(
            _settingsCatalog.Search(SettingsShell.MetadataPageSearch.Text));
        view.GroupDescriptions.Add(new PropertyGroupDescription(nameof(SettingsPageDescriptor.Group)));
        _refreshing = true;
        try
        {
            SettingsShell.MetadataPageList.ItemsSource = view;
            SettingsShell.MetadataPageList.SelectedValue = _viewModel.SelectedPageId;
        }
        finally { _refreshing = false; }
    }

    private void RefreshRoute()
    {
        if (_settingsCatalog is not null)
        {
            RefreshCatalogRoute();
            return;
        }
        var category = _viewModel.SelectedSection switch
        {
            SettingsSection.RolePackages or SettingsSection.Theme => SettingsSection.Personalization,
            SettingsSection.UserProfile or SettingsSection.ConversationMemory => SettingsSection.Privacy,
            SettingsSection.Speech or SettingsSection.AgentConnection => SettingsSection.ModelConnection,
            var section => section,
        };
        var isCapability = category == SettingsSection.ModelConnection;
        if (isCapability && !_capabilities.Any(page => page.PageId == _viewModel.SelectedPageId))
        {
            _viewModel.Navigate(_capabilities.FirstOrDefault()?.PageId ?? nameof(SettingsSection.Privacy));
            return;
        }
        if (isCapability) _lastCapabilityPageId = _viewModel.SelectedPageId;
        var item = isCapability
            ? _capabilities.First(page => page.PageId == _lastCapabilityPageId)
            : Categories.First(page => page.Section == category);
        _refreshing = true;
        try
        {
            SettingsNavigation.SelectedValue = category;
            SettingsShell.CapabilityTabs.SelectedValue = _settingsCatalog is null
                ? _capabilities.FirstOrDefault(page => page.PageId == _lastCapabilityPageId)?.Section
                : _lastCapabilityPageId;
        }
        finally { _refreshing = false; }
        SettingsShell.CapabilityBar.Visibility = isCapability && _settingsCatalog is null
            ? Visibility.Visible : Visibility.Collapsed;
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
        var pageId = isCapability ? _viewModel.SelectedPageId : contentSection.ToString();
        if (_settingsCatalog?.Pages.FirstOrDefault(page => page.Id == pageId) is { } pageDescriptor)
        {
            PageTitleText.Text = pageDescriptor.Title;
            PageDescriptionText.Text = pageDescriptor.Description;
            _refreshing = true;
            try { SettingsShell.MetadataPageList.SelectedValue = pageId; }
            finally { _refreshing = false; }
        }
        var pageKey = (pageId, route?.PackageId);
        if (_currentPage != pageKey)
        {
            // Apply queued scrolling before recording the outgoing page. This also
            // handles consecutive route changes within the same dispatcher turn.
            SettingsShell.Scroller.UpdateLayout();
            if (_currentPage is { } previous)
                _scrollOffsets[previous] = SettingsShell.Scroller.VerticalOffset;
            _currentPage = pageKey;
            SettingsContent.Content = _settingsCatalog is null
                ? _resolvePageContent(contentSection, route)
                : _settingsCatalog.CreateView(pageId, new(route?.PackageId, route?.DisplayName));
            var ownsScroll = SettingsContent.Content is ISettingsPageSurface { OwnsScrolling: true };
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

    private void RefreshCatalogRoute()
    {
        var catalog = _settingsCatalog!;
        var pageId = _viewModel.SelectedPageId;
        if (!catalog.Contains(pageId))
        {
            var fallback = catalog.Search(null).FirstOrDefault();
            if (fallback is null) { SettingsContent.Content = null; return; }
            _viewModel.Navigate(fallback.Id);
            return;
        }
        var descriptor = catalog.Pages.First(page => page.Id == pageId);
        var route = pageId == nameof(SettingsSection.RolePackages) ? _viewModel.PackageDetail : null;
        _refreshing = true;
        try { SettingsShell.MetadataPageList.SelectedValue = pageId; }
        finally { _refreshing = false; }
        SettingsShell.CapabilityBar.Visibility = Visibility.Collapsed;
        SettingsShell.HeaderText.Text = "设置";
        PageTitleText.Text = route?.DisplayName ?? descriptor.Title;
        PageDescriptionText.Text = descriptor.Description;
        PackageBreadcrumb.Visibility = route is null ? Visibility.Collapsed : Visibility.Visible;
        PackageBreadcrumbText.Text = route is null ? string.Empty : $"角色包 / {route.DisplayName}";
        var pageKey = (pageId, route?.PackageId);
        if (_currentPage == pageKey) return;
        SettingsShell.Scroller.UpdateLayout();
        if (_currentPage is { } previous)
            _scrollOffsets[previous] = SettingsShell.Scroller.VerticalOffset;
        _currentPage = pageKey;
        SettingsContent.Content = catalog.CreateView(pageId, new(route?.PackageId, route?.DisplayName));
        var ownsScroll = SettingsContent.Content is ISettingsPageSurface { OwnsScrolling: true };
        SettingsShell.Scroller.VerticalScrollBarVisibility = ownsScroll ? ScrollBarVisibility.Disabled : ScrollBarVisibility.Auto;
        SettingsContent.VerticalContentAlignment = ownsScroll ? VerticalAlignment.Stretch : VerticalAlignment.Top;
        SettingsShell.PageHeadingPanel.Visibility = ownsScroll ? Visibility.Collapsed : Visibility.Visible;
        SettingsShell.UpdateLayout();
        SettingsShell.Scroller.ScrollToVerticalOffset(_scrollOffsets.GetValueOrDefault(pageKey));
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (Dispatcher.HasShutdownStarted) return;
        e.Cancel = true;
        if (SettingsContent.Content is WebView2SurfaceHost host)
        {
            SettingsContent.Content = null;
            host.Dispose();
            _currentPage = null;
        }
        Hide();
    }

}
