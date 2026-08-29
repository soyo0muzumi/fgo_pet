using System.Runtime.ExceptionServices;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using FgoPet.App.Bootstrap;
using FgoPet.App.Settings;
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
}
