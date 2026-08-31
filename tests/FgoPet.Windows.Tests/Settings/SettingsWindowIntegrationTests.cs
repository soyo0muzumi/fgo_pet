using System.Runtime.ExceptionServices;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using FgoPet.App.Bootstrap;
using FgoPet.App.Dialogue;
using FgoPet.App.Servants;
using FgoPet.App.Settings;
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
            SettingsPageContentResolver resolver = (section, _) =>
            {
                var content = new TextBox { Text = section.ToString() };
                createdContent.Add(section, content);
                return content;
            };
            var window = new SettingsWindow(viewModel, resolver);
            try
            {
                Assert.Equal("设置", window.Title);
                Assert.NotNull(window.SettingsNavigation);
                Assert.NotNull(window.SettingsContent);
                Assert.Equal(8, window.SettingsNavigation.Items.Count);

                var profileContent = Assert.IsType<TextBox>(window.SettingsContent.Content);
                profileContent.Text = "unsaved session input";

                viewModel.Select(SettingsSection.Theme);
                Assert.Same(createdContent[SettingsSection.Theme], window.SettingsContent.Content);

                viewModel.Select(SettingsSection.UserProfile);
                Assert.Same(profileContent, window.SettingsContent.Content);
                Assert.Equal("unsaved session input", profileContent.Text);
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
            var packageList = new Border { Name = "PackageList" };
            var packageDetail = new Border { Name = "PackageDetail" };
            SettingsPageContentResolver resolver = (_, route) => route is null ? packageList : packageDetail;
            var window = new SettingsWindow(viewModel, resolver);
            try
            {
                Assert.Same(packageList, window.SettingsContent.Content);
                Assert.Equal(Visibility.Collapsed, window.PackageBreadcrumb.Visibility);

                viewModel.OpenPackageCommand.Execute(new PackageDetailRoute("official.mash", "Mash Kyrielight"));

                Assert.Same(packageDetail, window.SettingsContent.Content);
                Assert.Equal(Visibility.Visible, window.PackageBreadcrumb.Visibility);
                Assert.Equal("设置 / 角色包 / Mash Kyrielight", window.PackageBreadcrumbText.Text);

                viewModel.BackToPackagesCommand.Execute(null);

                Assert.Same(packageList, window.SettingsContent.Content);
                Assert.Equal(Visibility.Collapsed, window.PackageBreadcrumb.Visibility);
            }
            finally
            {
                window.Close();
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
            using var tray = new TrayService();
            var ui = new DesktopAppUi(
                tray, library, window, viewModel,
                null!, null!, null!, null!, null!);
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
                Assert.Equal(SettingsSection.UserProfile, viewModel.SelectedSection);
            }
            finally
            {
                window.Hide();
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
            using var tray = new TrayService();
            var ui = new DesktopAppUi(
                tray, library, window, viewModel,
                null!, null!, null!, null!, null!);
            try
            {
                var settingsItem = tray.Menu.Items.Cast<System.Windows.Forms.ToolStripItem>()
                    .Single(item => item.Text == "设置");
                settingsItem.PerformClick();

                Assert.True(window.IsVisible);
                Assert.Equal(SettingsSection.UserProfile, viewModel.SelectedSection);
            }
            finally
            {
                window.Hide();
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
            using var tray = new TrayService();
            var ui = new DesktopAppUi(
                tray, library, window, viewModel,
                null!, null!, null!, null!, null!, conversation);
            try
            {
                conversation.OpenSettingsCommand.Execute(null);

                Assert.True(window.IsVisible);
                Assert.Equal(SettingsSection.ModelConnection, viewModel.SelectedSection);
            }
            finally
            {
                window.Hide();
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
                Assert.IsType<UserProfilePage>(window.SettingsContent.Content);

                viewModel.Select(SettingsSection.Personalization);
                Assert.IsType<PersonalizationPage>(window.SettingsContent.Content);

                viewModel.Select(SettingsSection.Theme);
                Assert.IsType<ThemePage>(window.SettingsContent.Content);
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
            var store = new FakeSettingsStore(AppSettings.Defaults);
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
            var store = new FakeSettingsStore(AppSettings.Defaults);
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
            var profileContent = new Border { Name = "Profile" };
            var themeContent = new Border { Name = "Theme" };
            SettingsPageContentResolver resolver = (section, _) => section switch
            {
                SettingsSection.UserProfile => profileContent,
                SettingsSection.Theme => themeContent,
                _ => new Border { Name = section.ToString() },
            };
            var window = new SettingsWindow(viewModel, resolver);
            try
            {
                window.Show();
                var handle = new WindowInteropHelper(window).Handle;
                Assert.NotEqual(IntPtr.Zero, handle);

                window.SettingsNavigation.SelectedValue = SettingsSection.Theme;

                Assert.Equal(SettingsSection.Theme, viewModel.SelectedSection);
                Assert.Same(themeContent, window.SettingsContent.Content);
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

    private static void StaRun(Func<Task> action)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try { action().GetAwaiter().GetResult(); }
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
