using System.Windows;
using FgoPet.RenderingProbe.Art;

namespace FgoPet.RenderingProbe;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        try
        {
            var options = ProbeOptions.Parse(e.Args);
            var bundle = ArtBundleLoader.Load(options.BundlePath);
            var window = new MainWindow(options, bundle);
            MainWindow = window;
            window.Show();
        }
        catch (Exception error)
        {
            MessageBox.Show(error.Message, "FGO Pet Rendering Probe", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(2);
        }
    }
}
