using System.Collections;
using System.Windows.Controls;

namespace FgoPet.App.Settings.Views;

public partial class SettingsNavigationView : UserControl
{
    public SettingsNavigationView() => InitializeComponent();

    internal ListBox List => NavigationList;

    internal IEnumerable? ItemsSource
    {
        get => NavigationList.ItemsSource;
        set => NavigationList.ItemsSource = value;
    }

    internal object? SelectedValue
    {
        get => NavigationList.SelectedValue;
        set => NavigationList.SelectedValue = value;
    }
}
