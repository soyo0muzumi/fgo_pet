using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using FgoPet.RenderingProbe.Art;

namespace FgoPet.RenderingProbe.Rendering;

public interface IRenderSurface
{
    FrameworkElement View { get; }
    void Load(ArtBundle bundle);
    void SetExpression(string id);
    void ApplyGeometry(PortraitGeometry geometry);
    BitmapSource Capture(DpiScale dpi);
}
