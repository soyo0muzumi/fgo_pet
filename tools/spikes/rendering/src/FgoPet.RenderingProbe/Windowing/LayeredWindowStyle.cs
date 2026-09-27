using System.Windows;
using System.Windows.Interop;
using System.Windows.Shell;

namespace FgoPet.RenderingProbe.Windowing;

public sealed class LayeredWindowStyle : IDisposable
{
    private HwndSource? _source;

    public void Apply(Window window, TransparencyMode mode)
    {
        window.WindowStyle = WindowStyle.None;
        if (mode == TransparencyMode.Conventional)
        {
            window.AllowsTransparency = true;
            window.Background = System.Windows.Media.Brushes.Transparent;
            return;
        }

        window.AllowsTransparency = false;
        window.Background = null;
        WindowChrome.SetWindowChrome(window, new WindowChrome
        {
            CaptionHeight = 0,
            GlassFrameThickness = new Thickness(-1),
            ResizeBorderThickness = new Thickness(0),
            UseAeroCaptionButtons = false,
        });
        window.SourceInitialized += OnSourceInitialized;
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        if (sender is not Window window) return;
        _source = HwndSource.FromHwnd(new WindowInteropHelper(window).Handle);
        _source?.AddHook(Hook);
    }

    private static IntPtr Hook(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled) => IntPtr.Zero;

    public void Dispose()
    {
        _source?.RemoveHook(Hook);
        _source = null;
    }
}
