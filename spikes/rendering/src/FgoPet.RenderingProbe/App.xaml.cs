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
            _ = ArtBundleLoader.Load(options.BundlePath);
        }
        catch (Exception error)
        {
            MessageBox.Show(error.Message, "FGO Pet Rendering Probe", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(2);
        }
    }
}
