using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Input;
using System.Windows.Media;
using FgoPet.App.Bootstrap;
using FgoPet.App.Dialogue;
using FgoPet.App.Servants;
using FgoPet.App.Settings;
using FgoPet.App.Settings.Views;
using FgoPet.App.Theming;
using FgoPet.App.Tray;
using FgoPet.Core.Geometry;
using FgoPet.Core.Dialogue;
using FgoPet.Core.Packs;
using FgoPet.Core.Portraits;
using FgoPet.Core.Settings;
using FgoPet.Infrastructure.Dialogue;
using FgoPet.Infrastructure.Memory;
using FgoPet.Infrastructure.Packs;
using FgoPet.Infrastructure.Persistence;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace FgoPet.Windows.Tests.Settings;

[Trait("Category", "WindowsIntegration")]
public sealed class SettingsWindowIntegrationTests
{
    [Fact]
    public void Window_hosts_navigation_and_cached_page_content_in_one_shell()
    {
        StaRun(() =>
        {
            var viewModel = new SettingsViewModel();
            var createdContent = new Dictionary<SettingsSection, TextBox>();
            // The real resolver returns singletons, so re-selecting a category must not
            // rebuild its page; this also keeps the "cached content" contract testable.
            SettingsPageContentResolver resolver = (section, _) =>
            {
                if (!createdContent.TryGetValue(section, out var existing))
                {
                    existing = new TextBox { Text = section.ToString() };
                    createdContent.Add(section, existing);
                }
                return existing;
            };
            var window = new SettingsWindow(viewModel, resolver);
            try
            {
                Assert.Equal("FGO Pet · 设置", window.Title);
                Assert.NotNull(window.SettingsNavigation);
                Assert.NotNull(window.SettingsContent);
                // Five user-goal categories; Theme and UserProfile resolve into them.
                Assert.Equal(5, window.SettingsNavigation.Items.Count);

                var privacyContent = Assert.IsType<TextBox>(window.SettingsContent.Content);
                Assert.Equal(SettingsSection.Privacy.ToString(), privacyContent.Text);
                privacyContent.Text = "unsaved session input";

                viewModel.Select(SettingsSection.Theme);
                Assert.Same(createdContent[SettingsSection.Personalization], window.SettingsContent.Content);

                viewModel.Select(SettingsSection.UserProfile);
                Assert.Same(privacyContent, window.SettingsContent.Content);
                Assert.Equal("unsaved session input", privacyContent.Text);
                Assert.Equal(2, createdContent.Count);
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public void Package_route_replaces_content_and_back_returns_to_cached_package_list()
    {
        StaRun(() =>
        {
            var viewModel = new SettingsViewModel(SettingsSection.RolePackages);
            var packageList = new Border { Name = "PackageList", Height = 1200 };
            var packageDetail = new Border { Name = "PackageDetail" };
            SettingsPageContentResolver resolver = (_, route) => route is null ? packageList : packageDetail;
            var window = new SettingsWindow(viewModel, resolver);
            try
            {
                window.Show();
                window.UpdateLayout();
                Assert.Same(packageList, window.SettingsContent.Content);
                Assert.Equal(Visibility.Collapsed, window.PackageBreadcrumb.Visibility);
                var contentScroll = Descendants<ScrollViewer>(window)
                    .Single(scroller => ReferenceEquals(scroller.Content, window.SettingsContent));
                contentScroll.ScrollToVerticalOffset(180);
                window.UpdateLayout();
                Assert.Equal(180, contentScroll.VerticalOffset);

                viewModel.OpenPackageCommand.Execute(new PackageDetailRoute("official.mash", "Mash Kyrielight"));

                Assert.Same(packageDetail, window.SettingsContent.Content);
                Assert.Equal(Visibility.Visible, window.PackageBreadcrumb.Visibility);
                Assert.Equal("角色包 / Mash Kyrielight", window.PackageBreadcrumbText.Text);

                viewModel.BackToPackagesCommand.Execute(null);

                Assert.Same(packageList, window.SettingsContent.Content);
                Assert.Equal(Visibility.Collapsed, window.PackageBreadcrumb.Visibility);
                window.UpdateLayout();
                Assert.Equal(180, contentScroll.VerticalOffset);
            }
            finally
            {
                window.Hide();
            }
        });
    }

    [Fact]
    public void Visible_role_package_back_controls_follow_the_parent_route_without_recreating_pages()
    {
        StaRun(() =>
        {
            var viewModel = new SettingsViewModel(SettingsSection.RolePackages);
            var packageList = new RolePackagesPage(
                new ServantLibraryViewModel(
                    new PackageRepository(),
                    new PackageInstaller(),
                    new PortraitController(),
                    new FakeSettingsStore(AppSettings.Defaults),
                    _ => { }),
                viewModel);
            var personalization = new Border { Name = "Personalization" };
            RolePackageDetailPage? packageDetail = null;
            SettingsPageContentResolver resolver = (section, route) => section switch
            {
                SettingsSection.RolePackages when route is null => packageList,
                SettingsSection.RolePackages => packageDetail ??= new RolePackageDetailPage(
                    new RolePackageDetailViewModel(
                        route!,
                        (ServantLibraryViewModel)packageList.DataContext,
                        new FakeSettingsStore(AppSettings.Defaults),
                        viewModel)),
                _ => personalization,
            };
            var window = new SettingsWindow(viewModel, resolver);
            try
            {
                window.Show();
                window.UpdateLayout();

                var listBack = Descendants<Button>(packageList)
                    .Single(button => Equals(button.Content, "← 外观与角色"));
                listBack.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

                Assert.Equal(SettingsSection.Personalization, viewModel.SelectedSection);
                Assert.Same(personalization, window.SettingsContent.Content);

                viewModel.Select(SettingsSection.RolePackages);
                viewModel.OpenPackageCommand.Execute(new PackageDetailRoute("preview.mash", "玛修"));
                window.UpdateLayout();
                var detailBack = Descendants<Button>(packageDetail!)
                    .Single(button => Equals(button.Content, "← 角色包"));
                detailBack.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

                Assert.Equal(SettingsSection.RolePackages, viewModel.SelectedSection);
                Assert.Null(viewModel.PackageDetail);
                Assert.Same(packageList, window.SettingsContent.Content);
            }
            finally
            {
                window.Hide();
            }
        });
    }

    [Fact]
    public void Mouse_and_keyboard_reactivation_of_selected_appearance_category_returns_to_its_home()
    {
        StaRun(() =>
        {
            var viewModel = new SettingsViewModel(SettingsSection.RolePackages);
            var personalization = new Border { Name = "Personalization" };
            var packageList = new Border { Name = "PackageList" };
            var packageDetail = new Border { Name = "PackageDetail" };
            var window = new SettingsWindow(viewModel, (section, route) =>
                section == SettingsSection.RolePackages
                    ? route is null ? packageList : packageDetail
                    : personalization);
            try
            {
                window.Show();
                window.UpdateLayout();
                var appearanceItem = Assert.IsType<ListBoxItem>(
                    window.SettingsNavigation.ItemContainerGenerator.ContainerFromItem(
                        window.SettingsNavigation.SelectedItem));

                appearanceItem.RaiseEvent(new MouseButtonEventArgs(
                    Mouse.PrimaryDevice,
                    Environment.TickCount,
                    MouseButton.Left)
                {
                    RoutedEvent = Mouse.PreviewMouseUpEvent,
                    Source = appearanceItem,
                });

                Assert.Equal(SettingsSection.Personalization, viewModel.SelectedSection);
                Assert.Same(personalization, window.SettingsContent.Content);

                viewModel.OpenPackageCommand.Execute(new PackageDetailRoute("preview.mash", "玛修"));
                appearanceItem.Focus();
                appearanceItem.RaiseEvent(new KeyEventArgs(
                    Keyboard.PrimaryDevice,
                    PresentationSource.FromVisual(window),
                    Environment.TickCount,
                    Key.Enter)
                {
                    RoutedEvent = Keyboard.PreviewKeyDownEvent,
                    Source = appearanceItem,
                });

                Assert.Equal(SettingsSection.Personalization, viewModel.SelectedSection);
                Assert.Null(viewModel.PackageDetail);
                Assert.Same(personalization, window.SettingsContent.Content);
            }
            finally
            {
                window.Hide();
            }
        });
    }

    [Fact]
    public void Service_registration_uses_one_settings_shell_instance()
    {
        StaRun(() =>
        {
            using var provider = ServiceRegistration.AddFgoPet(new ServiceCollection(), []).BuildServiceProvider();

            var first = provider.GetRequiredService<SettingsWindow>();
            var second = provider.GetRequiredService<SettingsWindow>();

            Assert.Same(first, second);
            first.Close();
        });
    }

    [Fact]
    public void Tray_menu_exposes_settings_without_direct_model_or_library_entries()
    {
        StaRun(() =>
        {
            using var tray = new TrayService();
            var requested = false;
            tray.SettingsRequested += (_, _) => requested = true;

            var texts = tray.Menu.Items.Cast<System.Windows.Forms.ToolStripItem>()
                .Select(item => item.Text).ToArray();
            Assert.Contains("设置", texts);
            Assert.DoesNotContain(texts, text => text is "模型连接" or "从者库与设置");

            var settingsItem = tray.Menu.Items.Cast<System.Windows.Forms.ToolStripItem>()
                .Single(item => item.Text == "设置");
            settingsItem.PerformClick();

            Assert.True(requested);
        });
    }

    [Fact]
    public void Portrait_menu_exposes_settings_without_direct_model_or_library_entries()
    {
        StaRun(() =>
        {
            var viewModel = new SettingsViewModel();
            var window = new SettingsWindow(viewModel, (_, _) => new Border());
            var library = new ServantLibraryViewModel(
                new PackageRepository(),
                new PackageInstaller(),
                new PortraitController(),
                new FakeSettingsStore(AppSettings.Defaults),
                _ => { });
            var dialogueViewModel = new DialogueWindowViewModel(CreateConversationViewModel());
            var dialogueWindow = new DialogueWindow(dialogueViewModel);
            using var tray = new TrayService();
            var ui = new DesktopAppUi(
                tray, library, window, viewModel,
                null!, null!, null!, null!, null!,
                dialogueWindow: dialogueWindow, dialogue: dialogueViewModel);
            try
            {
                var headers = ui.PortraitMenu.Items.OfType<MenuItem>()
                    .Where(item => item.Header is string)
                    .Select(item => (string)item.Header)
                    .ToArray();
                Assert.Contains("设置", headers);
                Assert.DoesNotContain(headers, text => text is "模型连接" or "从者库与设置");

                var settingsItem = ui.PortraitMenu.Items.OfType<MenuItem>()
                    .Single(item => (string)item.Header == "设置");
                settingsItem.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));

                Assert.True(window.IsVisible);
                Assert.False(dialogueWindow.IsVisible);
                Assert.Equal(SettingsSection.Personalization, viewModel.SelectedSection);
                Assert.Null(window.Owner);
            }
            finally
            {
                window.Hide();
                dialogueWindow.Hide();
            }
        });
    }

    [Fact]
    public void Tray_settings_request_opens_the_settings_shell()
    {
        StaRun(() =>
        {
            var viewModel = new SettingsViewModel();
            var window = new SettingsWindow(viewModel, (_, _) => new Border());
            var library = new ServantLibraryViewModel(
                new PackageRepository(),
                new PackageInstaller(),
                new PortraitController(),
                new FakeSettingsStore(AppSettings.Defaults),
                _ => { });
            var dialogueViewModel = new DialogueWindowViewModel(CreateConversationViewModel());
            var dialogueWindow = new DialogueWindow(dialogueViewModel);
            using var tray = new TrayService();
            var ui = new DesktopAppUi(
                tray, library, window, viewModel,
                null!, null!, null!, null!, null!,
                dialogueWindow: dialogueWindow, dialogue: dialogueViewModel);
            try
            {
                var settingsItem = tray.Menu.Items.Cast<System.Windows.Forms.ToolStripItem>()
                    .Single(item => item.Text == "设置");
                settingsItem.PerformClick();

                Assert.True(window.IsVisible);
                Assert.False(dialogueWindow.IsVisible);
                Assert.Equal(SettingsSection.Personalization, viewModel.SelectedSection);
                Assert.Null(window.Owner);

                window.WindowState = WindowState.Minimized;
                settingsItem.PerformClick();

                Assert.Equal(WindowState.Normal, window.WindowState);
                Assert.Same(window, ui.GetType().GetField("_settingsWindow", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)?.GetValue(ui));
            }
            finally
            {
                window.Hide();
                dialogueWindow.Hide();
            }
        });
    }

    [Fact]
    public void Tray_show_hide_request_can_show_portrait_before_startup_show()
    {
        StaRun(() =>
        {
            var selection = new PortraitSelection("preview.mash", "casual", "1.0.0");
            var services = ServiceRegistration.AddFgoPet(new ServiceCollection(), []);
            services.AddSingleton<IAppSettingsStore>(new FakeSettingsStore(AppSettings.Defaults with { Selection = selection }));
            services.AddSingleton<PortraitActivation>((_, _) => Task.CompletedTask);
            using var provider = services.BuildServiceProvider();
            var ui = provider.GetRequiredService<DesktopAppUi>();
            var tray = provider.GetRequiredService<TrayService>();
            var portrait = provider.GetRequiredService<FgoPet.App.Main.PortraitWindow>();
            try
            {
                ui.InitializeTray();
                var showHideItem = tray.Menu.Items.Cast<System.Windows.Forms.ToolStripItem>()
                    .Single(item => item.Text == "显示/隐藏");

                showHideItem.PerformClick();
                portrait.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);

                Assert.True(portrait.IsVisible);
            }
            finally
            {
                portrait.Hide();
            }
        });
    }

    [Fact]
    public void Tray_show_hide_request_marshals_background_callback_to_portrait_dispatcher()
    {
        StaRun(() =>
        {
            var selection = new PortraitSelection("preview.mash", "casual", "1.0.0");
            var services = ServiceRegistration.AddFgoPet(new ServiceCollection(), []);
            services.AddSingleton<IAppSettingsStore>(new FakeSettingsStore(AppSettings.Defaults with { Selection = selection }));
            services.AddSingleton<PortraitActivation>((_, _) => Task.CompletedTask);
            using var provider = services.BuildServiceProvider();
            var ui = provider.GetRequiredService<DesktopAppUi>();
            var tray = provider.GetRequiredService<TrayService>();
            var portrait = provider.GetRequiredService<FgoPet.App.Main.PortraitWindow>();
            try
            {
                ui.InitializeTray();
                var showHideItem = tray.Menu.Items.Cast<System.Windows.Forms.ToolStripItem>()
                    .Single(item => item.Text == "显示/隐藏");

                var callback = Task.Run(showHideItem.PerformClick);
                while (!callback.IsCompleted)
                {
                    portrait.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.Background);
                }
                portrait.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.Background);

                Assert.Null(callback.Exception);
                Assert.True(portrait.IsVisible);
            }
            finally
            {
                portrait.Hide();
            }
        });
    }

    [Fact]
    public void Tray_show_request_uses_owned_portrait_when_lifetime_visibility_state_cannot_show_it()
    {
        StaRun(() =>
        {
            var selection = new PortraitSelection("preview.mash", "casual", "1.0.0");
            var services = ServiceRegistration.AddFgoPet(new ServiceCollection(), []);
            services.AddSingleton<FgoPet.App.Lifetime.IAppLifetime, NonDisplayingLifetime>();
            services.AddSingleton<IAppSettingsStore>(new FakeSettingsStore(AppSettings.Defaults with { Selection = selection }));
            services.AddSingleton<PortraitActivation>((_, _) => Task.CompletedTask);
            using var provider = services.BuildServiceProvider();
            var ui = provider.GetRequiredService<DesktopAppUi>();
            var tray = provider.GetRequiredService<TrayService>();
            var portrait = provider.GetRequiredService<FgoPet.App.Main.PortraitWindow>();
            try
            {
                ui.InitializeTray();

                tray.Menu.Items.Cast<System.Windows.Forms.ToolStripItem>()
                    .Single(item => item.Text == "显示/隐藏")
                    .PerformClick();

                Assert.True(portrait.IsVisible);
                Assert.NotEqual(IntPtr.Zero, new WindowInteropHelper(portrait).Handle);
            }
            finally
            {
                portrait.Hide();
            }
        });
    }

    [Fact]
    public void Tray_show_request_reactivates_saved_selection_when_portrait_state_is_cold()
    {
        StaRun(() =>
        {
            var selection = new PortraitSelection("preview.mash", "casual", "1.0.0");
            PortraitSelection? activated = null;
            var services = ServiceRegistration.AddFgoPet(new ServiceCollection(), []);
            services.AddSingleton<IAppSettingsStore>(new FakeSettingsStore(AppSettings.Defaults with { Selection = selection }));
            services.AddSingleton<PortraitActivation>((requested, _) =>
            {
                activated = requested;
                return Task.CompletedTask;
            });
            using var provider = services.BuildServiceProvider();
            var ui = provider.GetRequiredService<DesktopAppUi>();
            var tray = provider.GetRequiredService<TrayService>();
            var portrait = provider.GetRequiredService<FgoPet.App.Main.PortraitWindow>();
            try
            {
                ui.InitializeTray();

                tray.Menu.Items.Cast<System.Windows.Forms.ToolStripItem>()
                    .Single(item => item.Text == "显示/隐藏")
                    .PerformClick();
                portrait.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);

                Assert.Equal(selection, activated);
            }
            finally
            {
                portrait.Hide();
            }
        });
    }

    [Fact]
    public void Dialogue_settings_request_opens_the_model_connection_section()
    {
        StaRun(() =>
        {
            var viewModel = new SettingsViewModel();
            var window = new SettingsWindow(viewModel, (_, _) => new Border());
            var library = new ServantLibraryViewModel(
                new PackageRepository(),
                new PackageInstaller(),
                new PortraitController(),
                new FakeSettingsStore(AppSettings.Defaults),
                _ => { });
            var conversation = CreateConversationViewModel();
            var dialogueViewModel = new DialogueWindowViewModel(conversation);
            var dialogueWindow = new DialogueWindow(dialogueViewModel);
            using var tray = new TrayService();
            var ui = new DesktopAppUi(
                tray, library, window, viewModel,
                null!, null!, null!, null!, null!, conversation,
                dialogueWindow: dialogueWindow, dialogue: dialogueViewModel);
            try
            {
                dialogueWindow.Show();
                conversation.OpenSettingsCommand.Execute(null);

                Assert.True(window.IsVisible);
                Assert.True(dialogueWindow.IsVisible);
                Assert.Equal(SettingsSection.ModelConnection, viewModel.SelectedSection);
            }
            finally
            {
                window.Hide();
                dialogueWindow.Hide();
            }
        });
    }

    private static ConversationViewModel CreateConversationViewModel()
    {
        var settingsStore = new FakeSettingsStore(AppSettings.Defaults with
        {
            ModelConnection = new ModelConnectionSettings("test", "https://example.test/v1", "test-model"),
        });
        var orchestrator = new ConversationOrchestrator(
            new ThrowingProviderResolver(),
            new ThrowingContentResolver(),
            new SqliteConversationRepository(new RuntimeDatabase(":memory:")),
            new SqliteMemoryRepository(new RuntimeDatabase(":memory:")),
            new PromptComposer(),
            TimeProvider.System,
            settingsStore);
        return new ConversationViewModel(orchestrator, settingsStore);
    }

    private sealed class ThrowingProviderResolver : IChatProviderResolver
    {
        public IChatProvider Resolve() =>
            throw new FgoPet.Infrastructure.Providers.ProviderRequestException(
                FgoPet.Infrastructure.Providers.ProviderFailureCategory.Configuration, "未配置。");
    }

    private sealed class ThrowingContentResolver : IConversationContentResolver
    {
        public Task<ContentBinding> ResolveAsync(string servantId, CancellationToken cancellationToken) =>
            Task.FromResult(new ContentBinding(
                new ContentContextKey("stub", "stub.pack", "1.0.0", "default", "1", string.Empty),
                null,
                Array.Empty<KnowledgeEntry>(),
                Array.Empty<string>(),
                string.Empty,
                string.Empty));
    }

    [Fact]
    public void Service_registration_resolves_profile_personalization_and_theme_pages_in_the_same_shell()
    {
        StaRun(() =>
        {
            using var provider = ServiceRegistration.AddFgoPet(new ServiceCollection(), []).BuildServiceProvider();
            var window = provider.GetRequiredService<SettingsWindow>();
            var viewModel = provider.GetRequiredService<SettingsViewModel>();
            try
            {
                // UserProfile resolves into the "通用与数据" category.
                Assert.IsType<PrivacyPage>(window.SettingsContent.Content);

                viewModel.Select(SettingsSection.Personalization);
                Assert.IsType<PersonalizationPage>(window.SettingsContent.Content);

                // Theme has no category of its own: its control is embedded in the
                // personalization page, which keeps the reachable-entry contract.
                viewModel.Select(SettingsSection.Theme);
                var personalization = Assert.IsType<PersonalizationPage>(window.SettingsContent.Content);
                Assert.IsType<ThemePage>(personalization.ThemeHost.Content);
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public void Theme_page_reflects_immediate_selection_even_before_it_is_hosted()
    {
        StaRun(() =>
        {
            var resources = new ResourceDictionary();
            var store = new FakeSettingsStore(AppSettings.Defaults with { Theme = AppTheme.ModernGray });
            var themeService = new ThemeService(store, resources, ThemeService.CreateTestDictionary);
            themeService.Initialize();
            var page = new ThemePage(themeService);

            Assert.True(page.ModernGrayChoice.IsChecked);
            Assert.False(page.FgoLightChoice.IsChecked);
            Assert.True(page.ModernGrayChoice.Focusable);
            Assert.True(page.FgoLightChoice.Focusable);

            page.SelectTheme(AppTheme.FgoLight);

            Assert.Equal(AppTheme.FgoLight, themeService.CurrentTheme);
            Assert.False(page.ModernGrayChoice.IsChecked);
            Assert.True(page.FgoLightChoice.IsChecked);
            Assert.Equal(AppTheme.FgoLight, store.Load().Theme);
        });
    }

    [Fact]
    public void Theme_page_resyncs_selection_and_status_when_theme_resource_loading_fails()
    {
        StaRun(() =>
        {
            var resources = new ResourceDictionary();
            var store = new FakeSettingsStore(AppSettings.Defaults with { Theme = AppTheme.ModernGray });
            var themeService = new ThemeService(
                store,
                resources,
                theme => theme == AppTheme.FgoLight
                    ? throw new InvalidOperationException("simulated resource failure")
                    : ThemeService.CreateTestDictionary(theme));
            themeService.Initialize();
            var page = new ThemePage(themeService);

            page.FgoLightChoice.IsChecked = true;

            Assert.Equal(AppTheme.ModernGray, themeService.CurrentTheme);
            Assert.True(page.ModernGrayChoice.IsChecked);
            Assert.False(page.FgoLightChoice.IsChecked);
            Assert.Contains("加载失败", page.StatusText, StringComparison.Ordinal);
        });
    }

    [Fact]
    public void Actual_navigation_selection_updates_view_model_content_and_same_window_route()
    {
        StaRun(() =>
        {
            var viewModel = new SettingsViewModel();
            var personalizationContent = new Border { Name = "Personalization" };
            SettingsPageContentResolver resolver = (section, _) => section switch
            {
                SettingsSection.Personalization => personalizationContent,
                _ => new Border { Name = section.ToString() },
            };
            var window = new SettingsWindow(viewModel, resolver);
            try
            {
                window.Show();
                var handle = new WindowInteropHelper(window).Handle;
                Assert.NotEqual(IntPtr.Zero, handle);

                window.SettingsNavigation.SelectedValue = SettingsSection.Personalization;

                Assert.Equal(SettingsSection.Personalization, viewModel.SelectedSection);
                Assert.Same(personalizationContent, window.SettingsContent.Content);
                Assert.Equal(handle, new WindowInteropHelper(window).Handle);
            }
            finally
            {
                window.Hide();
            }
        });
    }

    [Fact]
    public void Embedded_role_package_list_loads_cards_and_open_button_routes_in_the_same_window()
    {
        StaRun(async () =>
        {
            var settingsStore = new FakeSettingsStore(AppSettings.Defaults);
            var library = new ServantLibraryViewModel(
                new PackageRepository(),
                new PackageInstaller(),
                new PortraitController(),
                settingsStore,
                _ => { });
            var viewModel = new SettingsViewModel(SettingsSection.RolePackages);
            var packageList = new RolePackagesPage(library, viewModel);
            RolePackageDetailPage? createdDetail = null;
            SettingsPageContentResolver resolver = (_, route) => route is null
                ? packageList
                : createdDetail = new RolePackageDetailPage(
                    new RolePackageDetailViewModel(route, library, settingsStore, viewModel));
            var window = new SettingsWindow(viewModel, resolver);
            try
            {
                await packageList.RefreshAsync();
                window.Show();
                window.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
                window.UpdateLayout();

                var card = Assert.Single(packageList.PackageList.Items.Cast<ServantCardViewModel>());
                Assert.Equal("preview.mash", card.PackageId);
                Assert.Equal("1.0.0", card.PackageVersion);
                Assert.Equal("来源未验证", card.SourceBadge);
                var handle = new WindowInteropHelper(window).Handle;
                var openButton = Descendants<Button>(window)
                    .Single(button => Equals(button.Content, "打开角色包"));

                openButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

                Assert.Equal(new PackageDetailRoute("preview.mash", "玛修"), viewModel.PackageDetail);
                Assert.Same(createdDetail, window.SettingsContent.Content);
                Assert.NotEqual(IntPtr.Zero, handle);
                Assert.Equal(handle, new WindowInteropHelper(window).Handle);

                var detail = Assert.IsType<RolePackageDetailViewModel>(createdDetail!.DataContext);
                detail.CustomAddress = "unsaved address";
                viewModel.Select(SettingsSection.Theme);
                viewModel.Select(SettingsSection.RolePackages);
                Assert.Same(createdDetail, window.SettingsContent.Content);
                Assert.Equal("unsaved address", detail.CustomAddress);
            }
            finally
            {
                window.Hide();
            }
        });
    }

    [Fact]
    public void Appearance_page_exposes_soft_purple_hierarchy_and_semantic_preview_fallback()
    {
        StaRun(() =>
        {
            var page = new PersonalizationPage(
                new PersonalizationViewModel(new FakeSettingsStore(AppSettings.Defaults)),
                CreateLibrary(),
                new SettingsViewModel());
            try
            {
                page.Resources["TextBrush"] = new SolidColorBrush(Colors.Red);
                page.Resources["SurfaceBrush"] = new SolidColorBrush(Colors.Red);
                page.Resources.MergedDictionaries.Insert(0, new ResourceDictionary
                {
                    Source = new Uri("/FgoPet.App;component/Themes/FgoLight.xaml", UriKind.Relative),
                });
                page.Measure(new Size(640, 480));
                page.Arrange(new Rect(0, 0, 640, 480));
                page.UpdateLayout();

                Assert.Contains(Descendants<TextBlock>(page), text => text.Text == "外观与角色");
                Assert.Contains(Descendants<TextBlock>(page), text => text.Text == "当前角色");
                Assert.Contains(Descendants<TextBlock>(page), text => text.Text == "未提供预览");

                var currentRole = Descendants<TextBlock>(page).Single(text => text.Text == "当前角色");
                Assert.Equal(Color.FromRgb(0x25, 0x21, 0x37), Assert.IsType<SolidColorBrush>(currentRole.Foreground).Color);
            }
            finally
            {
                page.RaiseEvent(new RoutedEventArgs(FrameworkElement.UnloadedEvent));
            }
        });
    }

    [Fact]
    public async Task Role_package_pages_keep_trust_copy_and_single_shell_scroll_with_wrapped_detail_text()
    {
        await StaRun(async () =>
        {
            var library = CreateLibrary();
            var settings = new SettingsViewModel(SettingsSection.RolePackages);
            var list = new RolePackagesPage(library, settings);
            var detail = new RolePackageDetailPage(
                new RolePackageDetailViewModel(
                    new PackageDetailRoute("preview.mash", "玛修"),
                    library,
                    new FakeSettingsStore(AppSettings.Defaults),
                    settings));
            try
            {
                await library.LoadAsync();
                list.Measure(new Size(640, 640));
                list.Arrange(new Rect(0, 0, 640, 640));
                list.UpdateLayout();
                detail.Measure(new Size(640, 640));
                detail.Arrange(new Rect(0, 0, 640, 640));
                detail.UpdateLayout();
                Assert.Contains(Descendants<TextBlock>(list), text => text.Text == "来源、兼容与隐私");
                Assert.Contains(Descendants<TextBlock>(list), text => text.Text?.Contains("仅处理本机", StringComparison.Ordinal) == true);
                Assert.Contains(Descendants<TextBlock>(list), text => text.Text == "未提供预览");
                Assert.Contains(Descendants<Button>(list), button => Equals(button.Content, "安装"));
                Assert.Contains(Descendants<Button>(list), button => Equals(button.Content, "重新扫描"));
                Assert.Contains(Descendants<Button>(list), button => Equals(button.Content, "打开角色包目录"));
                var card = Assert.Single(library.FilteredServants);
                Assert.Equal("来源未验证", card.SourceBadge);

                Assert.Empty(Descendants<ScrollViewer>(detail));
                var summary = Descendants<TextBlock>(detail).Single(text => text.Text == "包摘要");
                Assert.Equal(TextWrapping.Wrap, summary.TextWrapping);
                Assert.Contains(Descendants<TextBlock>(detail), text => text.Text == "未提供预览");
                detail.Measure(new Size(496, 480));
                Assert.True(detail.DesiredSize.Width <= 496, $"detail width was {detail.DesiredSize.Width}");
                var window = new SettingsWindow(settings, (_, route) => route is null ? list : detail);
                Assert.Equal(680d, window.MinWidth);
                Assert.Equal(520d, window.MinHeight);
                await detail.RefreshAsync();
                Assert.Equal("未声明最低应用版本", ((RolePackageDetailViewModel)detail.DataContext).CompatibilityText);
            }
            finally
            {
                list.RaiseEvent(new RoutedEventArgs(FrameworkElement.UnloadedEvent));
                detail.RaiseEvent(new RoutedEventArgs(FrameworkElement.UnloadedEvent));
            }
        });
    }

    [Fact]
    public void Service_registration_resolves_role_package_list_and_details_without_a_legacy_window()
    {
        StaRun(() =>
        {
            using var provider = ServiceRegistration.AddFgoPet(new ServiceCollection(), []).BuildServiceProvider();
            var window = provider.GetRequiredService<SettingsWindow>();
            var viewModel = provider.GetRequiredService<SettingsViewModel>();
            try
            {
                viewModel.Select(SettingsSection.RolePackages);
                Assert.IsType<RolePackagesPage>(window.SettingsContent.Content);

                viewModel.OpenPackageCommand.Execute(new PackageDetailRoute("official.mash", "玛修"));
                Assert.IsType<RolePackageDetailPage>(window.SettingsContent.Content);
                Assert.Null(typeof(SettingsWindow).Assembly.GetType("FgoPet.App.Servants.ServantLibraryWindow"));
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public void Settings_shell_matches_the_html_first_geometry()
    {
        StaRun(() =>
        {
            var viewModel = new SettingsViewModel();
            var window = new SettingsWindow(viewModel, (_, _) => new Border());
            try
            {
                Assert.Equal("FGO Pet · 设置", window.Title);
                var shell = Assert.IsType<SettingsShellView>(window.FindName("SettingsShell"));
                // The header height lives on the shell's fixed 86 DIP row, not on the border.
                shell.Measure(new Size(960, 720));
                shell.Arrange(new Rect(0, 0, 960, 720));
                shell.UpdateLayout();
                Assert.Equal(86d, shell.Header.ActualHeight);
                Assert.Equal("设置", shell.HeaderText.Text);
                Assert.Equal(new GridLength(184), shell.NavigationColumn.Width);
                Assert.Equal(new Thickness(28, 28, 28, 20), shell.Body.Margin);
                // The window is built without the application resource dictionaries, so the
                // shell controls are supplied here before asserting on their styles.
                shell.Resources.MergedDictionaries.Insert(0, new ResourceDictionary
                {
                    Source = new Uri("/FgoPet.App;component/Ui/Shell/ShellControls.xaml", UriKind.Relative),
                });
                Assert.NotNull(shell.TryFindResource("ShellToolbarButtonStyle"));
                Assert.NotNull(shell.TryFindResource("ShellSurfaceStyle"));

                var captureDirectory = Environment.GetEnvironmentVariable("FGO_PET_UI_CAPTURE_DIR");
                if (!string.IsNullOrWhiteSpace(captureDirectory))
                {
                    System.IO.Directory.CreateDirectory(captureDirectory);
                    shell.Resources.MergedDictionaries.Insert(0, new ResourceDictionary
                    {
                        Source = new Uri("/FgoPet.App;component/Themes/ModernGray.xaml", UriKind.Relative),
                    });
                    shell.Measure(new Size(960, 720));
                    shell.Arrange(new Rect(0, 0, 960, 720));
                    shell.UpdateLayout();
                    var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap(
                        960, 720, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
                    bitmap.Render(shell);
                    var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
                    encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
                    using var file = System.IO.File.Create(System.IO.Path.Combine(captureDirectory, "settings-shell.png"));
                    encoder.Save(file);
                }
            }
            finally
            {
                window.Close();
            }
        });
    }
    [Theory]
    [InlineData("ModernGray", "#FF16131D")]
    [InlineData("FgoLight", "#FFF7F6FB")]
    public void Settings_shell_uses_the_approved_html_palette(string theme, string expectedBackground)
    {
        StaRun(() =>
        {
            var shell = new SettingsShellView();
            // Shell brushes come from ShellTokens and take their colours from the theme,
            // so both dictionaries are needed for the palette to resolve.
            shell.Resources.MergedDictionaries.Insert(0, new ResourceDictionary
            {
                Source = new Uri("/FgoPet.App;component/Ui/Shell/ShellTokens.xaml", UriKind.Relative),
            });
            shell.Resources.MergedDictionaries.Insert(1, new ResourceDictionary
            {
                Source = new Uri($"/FgoPet.App;component/Themes/{theme}.xaml", UriKind.Relative),
            });
            shell.Measure(new Size(960, 720));
            shell.Arrange(new Rect(0, 0, 960, 720));
            shell.UpdateLayout();

            var expected = (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(expectedBackground);
            var actual = Assert.IsType<System.Windows.Media.SolidColorBrush>(shell.Background).Color;
            Assert.Equal(expected, actual);
        });
    }
    private static ServantLibraryViewModel CreateLibrary() => new(
        new PackageRepository(),
        new PackageInstaller(),
        new PortraitController(),
        new FakeSettingsStore(AppSettings.Defaults),
        _ => { });

    private static void StaRun(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try { action(); }
            catch (Exception error) { failure = error; }
            finally
            {
                var dispatcher = System.Windows.Threading.Dispatcher.FromThread(Thread.CurrentThread);
                if (dispatcher is not null && !dispatcher.HasShutdownStarted)
                {
                    dispatcher.InvokeShutdown();
                }
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
    }

    private static Task StaRun(Func<Task> action)
    {
        var completion = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                action().GetAwaiter().GetResult();
                completion.SetResult(null);
            }
            catch (Exception error) { completion.SetException(error); }
            finally
            {
                var dispatcher = System.Windows.Threading.Dispatcher.FromThread(Thread.CurrentThread);
                if (dispatcher is not null && !dispatcher.HasShutdownStarted)
                {
                    dispatcher.InvokeShutdown();
                }
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return completion.Task;
    }

    private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            var child = VisualTreeHelper.GetChild(root, index);
            if (child is T match)
            {
                yield return match;
            }

            foreach (var descendant in Descendants<T>(child))
            {
                yield return descendant;
            }
        }
    }

    private sealed class PackageRepository : IArtPackageRepository
    {
        public Task<IReadOnlyList<InstalledServant>> ListServantsAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<InstalledServant>>
            ([
                new InstalledServant(
                    "preview.mash",
                    "mash_kyrielight",
                    "玛修",
                    "C:\\packs\\mash\\previews\\library.png",
                    "community",
                    [new ServantAppearance("casual", "1.0.0", "C:\\packs\\mash", null)]),
            ]);

        public Task<PackCatalog> ScanAsync(CancellationToken cancellationToken) =>
            Task.FromResult(new PackCatalog([]));
        public Task<AppearanceLocation?> GetAppearanceAsync(PortraitSelection selection, CancellationToken cancellationToken) =>
            Task.FromResult<AppearanceLocation?>(null);
        public Task<AppearanceLocation?> ResolveStartupSelectionAsync(PortraitSelection? requested, CancellationToken cancellationToken) =>
            Task.FromResult<AppearanceLocation?>(null);
        public Task<bool> RemoveAsync(string packageId, string packageVersion, CancellationToken cancellationToken) =>
            Task.FromResult(false);
        public Task MarkLastKnownGoodAsync(PortraitSelection selection, CancellationToken cancellationToken) =>
            Task.CompletedTask;
    }

    private sealed class PackageInstaller : IPackInstaller
    {
        public Task<PackInstallResult> InstallAsync(string archivePath, CancellationToken cancellationToken) =>
            Task.FromResult(new PackInstallResult(true, new PackIdentity("preview.mash", "1.0.0"), null));
    }

    private sealed class PortraitController : IPortraitController
    {
        public Task ActivateAsync(PortraitSelection selection, CancellationToken cancellationToken) => Task.CompletedTask;
        public void SetExpression(ExpressionSemantic semantic) { }
        public void SetScale(double scale) { }
        public void ApplyDpi(Dpi2 dpi) { }
    }

    private sealed class FakeSettingsStore(AppSettings initial) : IAppSettingsStore
    {
        private AppSettings _settings = initial;

        public string Location => "memory";

        public AppSettings Load() => _settings;

        public void Save(AppSettings settings) => _settings = settings;
    }

    private sealed class NonDisplayingLifetime : FgoPet.App.Lifetime.IAppLifetime
    {
        public bool IsPetVisible => false;
        public void AttachPetWindow(Window window) { }
        public void ShowOrHidePet() { }
        public void RequestNormalExit() { }
        public void Shutdown(int exitCode) { }
    }
}
