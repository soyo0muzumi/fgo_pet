using System.Windows;
using System.Windows.Controls;
using FgoPet.App.ViewModels;

namespace FgoPet.App.Views;

public partial class ArchiveDraftCard : UserControl
{
    public ArchiveDraftCard() => InitializeComponent();

    private void OnConfirmClick(object sender, RoutedEventArgs e) =>
        (DataContext as ArchiveDraftViewModel)?.Confirm();
}
