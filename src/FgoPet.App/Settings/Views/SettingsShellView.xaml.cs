using System.Windows;
using System.Windows.Controls;

namespace FgoPet.App.Settings.Views;

public partial class SettingsShellView : UserControl
{
    public SettingsShellView() => InitializeComponent();

    internal ListBox SettingsNavigation => Navigation.List;
    internal ContentControl ContentHost => SettingsContent;
    internal FrameworkElement Breadcrumb => PackageBreadcrumb;
    internal TextBlock BreadcrumbText => PackageBreadcrumbText;
    internal TextBlock PageTitle => PageTitleText;
    internal TextBlock PageDescription => PageDescriptionText;
    internal ColumnDefinition NavigationColumn => SettingsNavigationColumn;
    internal Grid Body => SettingsBody;
    internal Border Header => ProductHeader;
    internal TextBlock HeaderText => ProductHeaderText;
}
