using System.Runtime.ExceptionServices;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using FgoPet.App.Bootstrap;
using FgoPet.App.Settings;
using FgoPet.App.Theming;
using FgoPet.Core.Settings;
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
                Assert.Equal(7, window.SettingsNavigation.Items.Count);

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
    public void Desktop_ui_routes_repeated_settings_requests_to_the_same_shell()
    {
        StaRun(() =>
        {
            var viewModel = new SettingsViewModel();
            var window = new SettingsWindow(viewModel, (_, _) => new Border());
            var ui = new DesktopAppUi(
                null!, null!, null!, null!, window, viewModel,
                null!, null!, null!, null!, null!);
            try
            {
                ui.ShowSettings(SettingsSection.Theme);
                var firstHandle = new WindowInteropHelper(window).Handle;

                ui.ShowSettings(SettingsSection.ModelConnection);

                Assert.True(window.IsVisible);
                Assert.Equal(SettingsSection.ModelConnection, viewModel.SelectedSection);
                Assert.NotEqual(IntPtr.Zero, firstHandle);
                Assert.Equal(firstHandle, new WindowInteropHelper(window).Handle);
            }
            finally
            {
                window.Hide();
            }
        });
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

    private static void StaRun(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try { action(); }
            catch (Exception error) { failure = error; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
    }

    private sealed class FakeSettingsStore(AppSettings initial) : IAppSettingsStore
    {
        private AppSettings _settings = initial;

        public string Location => "memory";

        public AppSettings Load() => _settings;

        public void Save(AppSettings settings) => _settings = settings;
    }
}
