using FgoPet.App.Settings;
using Xunit;

namespace FgoPet.App.Tests.Settings;

public sealed class SettingsViewModelTests
{
    [Fact]
    public void Navigation_exposes_the_seven_destinations_in_product_order()
    {
        var viewModel = new SettingsViewModel();

        Assert.Collection(
            viewModel.NavigationItems,
            item => AssertItem(item, SettingsSection.UserProfile, "用户资料", "IconProfileGeometry"),
            item => AssertItem(item, SettingsSection.Personalization, "个性化", "IconPersonalizationGeometry"),
            item => AssertItem(item, SettingsSection.RolePackages, "角色包", "IconRolePackageGeometry"),
            item => AssertItem(item, SettingsSection.ModelConnection, "AI 模型与连接", "IconConnectionGeometry"),
            item => AssertItem(item, SettingsSection.AgentConnection, "Agent 连接", "IconAgentGeometry"),
            item => AssertItem(item, SettingsSection.ConversationMemory, "对话与记忆", "IconConversationGeometry"),
            item => AssertItem(item, SettingsSection.Privacy, "数据与隐私", "IconPrivacyGeometry"),
            item => AssertItem(item, SettingsSection.Theme, "主题", "IconThemeGeometry"));
    }

    [Fact]
    public void Select_changes_the_active_section_and_header()
    {
        var viewModel = new SettingsViewModel();

        viewModel.Select(SettingsSection.ModelConnection);

        Assert.Equal(SettingsSection.ModelConnection, viewModel.SelectedSection);
        Assert.Equal("AI 模型与连接", viewModel.PageTitle);
        Assert.False(string.IsNullOrWhiteSpace(viewModel.PageDescription));
    }

    [Fact]
    public void Package_detail_is_an_in_shell_route_with_breadcrumb_and_back_state()
    {
        var viewModel = new SettingsViewModel();
        var route = new PackageDetailRoute("official.mash", "Mash Kyrielight");

        viewModel.OpenPackageCommand.Execute(route);

        Assert.Equal(SettingsSection.RolePackages, viewModel.SelectedSection);
        Assert.Same(route, viewModel.PackageDetail);
        Assert.Equal("Mash Kyrielight", viewModel.PageTitle);
        Assert.Equal("设置 / 角色包 / Mash Kyrielight", viewModel.Breadcrumb);

        viewModel.Select(SettingsSection.Theme);
        viewModel.Select(SettingsSection.RolePackages);
        Assert.Same(route, viewModel.PackageDetail);

        viewModel.BackToPackagesCommand.Execute(null);

        Assert.Null(viewModel.PackageDetail);
        Assert.Equal("角色包", viewModel.PageTitle);
        Assert.Null(viewModel.Breadcrumb);
    }

    private static void AssertItem(
        SettingsNavigationItem item,
        SettingsSection section,
        string label,
        string iconKey)
    {
        Assert.Equal(section, item.Section);
        Assert.Equal(label, item.Label);
        Assert.Equal(iconKey, item.IconKey);
        Assert.False(string.IsNullOrWhiteSpace(item.Description));
    }
}
