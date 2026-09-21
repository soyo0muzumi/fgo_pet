using System.Windows.Controls;

namespace FgoPet.App.Settings;

public partial class UserProfilePage : UserControl
{
    public UserProfilePage(UserProfileViewModel viewModel)
    {
        ArgumentNullException.ThrowIfNull(viewModel);
        InitializeComponent();
        ViewModel = viewModel;
        DataContext = viewModel;
    }

    public UserProfileViewModel ViewModel { get; }
}
