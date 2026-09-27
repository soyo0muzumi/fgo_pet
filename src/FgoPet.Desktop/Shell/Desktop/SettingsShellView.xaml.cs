using System.Windows;
using System.Windows.Controls;

namespace FgoPet.App.Settings.Views;

public partial class SettingsShellView : UserControl
{
    public SettingsShellView() => InitializeComponent();

    internal ListBox SettingsNavigation => Navigation.List;
    internal ListBox CapabilityTabs => CapabilityNavigation.List;
    internal FrameworkElement CapabilityBar => CapabilityNavigation;
    internal ScrollViewer Scroller => PageScroller;
    internal FrameworkElement PageHeadingPanel => PageHeading;
    internal Button ChatButton => ChatNavigationButton;
    internal Button TodoButton => TodoNavigationButton;
    internal Button FocusButton => FocusNavigationButton;
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
