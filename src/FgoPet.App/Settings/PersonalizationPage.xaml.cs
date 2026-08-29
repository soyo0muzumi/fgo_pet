using System.Windows.Controls;

namespace FgoPet.App.Settings;

public partial class PersonalizationPage : UserControl
{
    public PersonalizationPage(PersonalizationViewModel viewModel)
    {
        ArgumentNullException.ThrowIfNull(viewModel);
        InitializeComponent();
        ViewModel = viewModel;
        DataContext = viewModel;
    }

    public PersonalizationViewModel ViewModel { get; }
}
