using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using FgoPet.RenderingProbe.Art;
using FgoPet.RenderingProbe.Diagnostics;
using FgoPet.RenderingProbe.Rendering;
using FgoPet.RenderingProbe.Windowing;

namespace FgoPet.RenderingProbe;

public partial class MainWindow : Window
{
    private readonly ProbeOptions _options;
    private readonly ArtBundle _bundle;
    private readonly ProbeRecorder _recorder;
    private readonly LayeredWindowStyle _windowStyle = new();
    private readonly string[] _expressions;
    private IRenderSurface _surface;
    private double _scale;
    private int _expressionIndex;
    private bool _todoMode;

    public MainWindow(ProbeOptions options, ArtBundle bundle)
    {
        InitializeComponent();
        _options = options;
        _bundle = bundle;
        _scale = options.Scale;
        _recorder = new ProbeRecorder(options.OutputDirectory);
        _expressions = bundle.Images.Keys.Where(id => id != bundle.Composition.BodyId).OrderBy(id => id, StringComparer.Ordinal).ToArray();
        _expressionIndex = Array.IndexOf(_expressions, bundle.Composition.DefaultExpressionId);
        _surface = CreateSurface(options.Backend);
        _surface.Load(bundle);
        PortraitHost.Content = _surface.View;
        _windowStyle.Apply(this, options.Transparency);
        Closed += (_, _) =>
        {
            _windowStyle.Dispose();
            if (_surface is IDisposable disposable) disposable.Dispose();
        };
        Loaded += (_, _) => ApplyLayout();
    }

    private IRenderSurface CreateSurface(RenderBackend backend) =>
        backend == RenderBackend.Wpf ? new WpfPortraitSurface() : new SkiaPortraitSurface();

    private void ApplyLayout()
    {
        var dpi = VisualTreeHelper.GetDpi(this);
        var geometry = PortraitLayout.Calculate(_bundle, _scale, dpi);
        _surface.ApplyGeometry(geometry);
        var panelWidth = 440 * _scale;
        var panelHeight = 160 * _scale;
        TerminalPanel.Width = panelWidth;
        TerminalPanel.Height = panelHeight;
        var minimumX = Math.Min(0, geometry.PanelAnchor.X - panelWidth / 2);
        var shiftX = -minimumX;
        Canvas.SetLeft(PortraitHost, shiftX);
        Canvas.SetTop(PortraitHost, 0);
        Canvas.SetLeft(TerminalPanel, shiftX + geometry.PanelAnchor.X - panelWidth / 2);
        Canvas.SetTop(TerminalPanel, geometry.PanelAnchor.Y);
        Root.Width = Math.Max(geometry.LogicalSize.Width + shiftX, panelWidth);
        Root.Height = Math.Max(geometry.LogicalSize.Height, geometry.PanelAnchor.Y + panelHeight);
        DiagnosticText.Text = $"{_options.Backend} / {_options.Transparency}\n{_expressions[_expressionIndex]}  scale={_scale:0.##} dpi={dpi.DpiScaleX:0.##}";
    }

    private void OnDpiChanged(object sender, DpiChangedEventArgs e) =>
        Dispatcher.InvokeAsync(ApplyLayout, DispatcherPriority.Loaded);

    private async void OnKeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Escape: Close(); break;
            case Key.Left: ChangeExpression(-1); break;
            case Key.Right: ChangeExpression(1); break;
            case Key.D1: _scale = 0.5; ApplyLayout(); break;
            case Key.D2: _scale = 0.6; ApplyLayout(); break;
            case Key.D3: _scale = 0.75; ApplyLayout(); break;
            case Key.P: TerminalPanel.Visibility = TerminalPanel.Visibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible; break;
            case Key.T: TogglePanelText(); break;
            case Key.C: await SaveCaptureAsync(); break;
            case Key.R: await RunStressAsync(); break;
            case Key.F1: SwitchBackend(RenderBackend.Wpf); break;
            case Key.F2: SwitchBackend(RenderBackend.Skia); break;
        }
    }

    private void ChangeExpression(int delta)
    {
        _expressionIndex = (_expressionIndex + delta + _expressions.Length) % _expressions.Length;
        var stopwatch = Stopwatch.StartNew();
        _surface.SetExpression(_expressions[_expressionIndex]);
        stopwatch.Stop();
        Record(stopwatch.Elapsed.TotalMilliseconds);
        ApplyLayout();
    }

    private void SwitchBackend(RenderBackend backend)
    {
        if ((_surface is WpfPortraitSurface) == (backend == RenderBackend.Wpf)) return;
        if (_surface is IDisposable disposable) disposable.Dispose();
        _surface = CreateSurface(backend);
        _surface.Load(_bundle);
        _surface.SetExpression(_expressions[_expressionIndex]);
        PortraitHost.Content = _surface.View;
        ApplyLayout();
    }

    private void TogglePanelText()
    {
        _todoMode = !_todoMode;
        PanelText.Text = _todoMode
            ? "TODAY\n□ 完成一次素材巡检   □ 整理对话草稿"
            : "前辈，今天也请按自己的节奏来。";
    }

    private async Task SaveCaptureAsync()
    {
        await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Render);
        var dpi = VisualTreeHelper.GetDpi(this);
        var width = Math.Max(1, (int)Math.Ceiling(Root.ActualWidth * dpi.DpiScaleX));
        var height = Math.Max(1, (int)Math.Ceiling(Root.ActualHeight * dpi.DpiScaleY));
        var capture = new RenderTargetBitmap(width, height, 96 * dpi.DpiScaleX, 96 * dpi.DpiScaleY, PixelFormats.Pbgra32);
        capture.Render(Root);
        var directory = Path.Combine(_options.OutputDirectory, "captures");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, $"{DateTimeOffset.Now:yyyyMMdd-HHmmssfff}-{_expressions[_expressionIndex]}.png");
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(capture));
        using var stream = File.Create(path);
        encoder.Save(stream);
    }

    private async Task RunStressAsync()
    {
        var workingSets = new List<long>();
        foreach (var id in StressSequence.Create(_bundle.Images.Keys))
        {
            var stopwatch = Stopwatch.StartNew();
            _surface.SetExpression(id);
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Render);
            stopwatch.Stop();
            _expressionIndex = Array.IndexOf(_expressions, id);
            workingSets.Add(Process.GetCurrentProcess().WorkingSet64);
            Record(stopwatch.Elapsed.TotalMilliseconds);
        }
        var final = Process.GetCurrentProcess().WorkingSet64;
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        _recorder.WriteSummary(new SessionSummary(workingSets.Min(), workingSets.Max(), final, Process.GetCurrentProcess().WorkingSet64, workingSets.Count));
        ApplyLayout();
    }

    private void Record(double milliseconds)
    {
        var dpi = VisualTreeHelper.GetDpi(this);
        _recorder.Append(new ProbeSample(
            DateTimeOffset.Now,
            _surface is WpfPortraitSurface ? "wpf" : "skia",
            _options.Transparency.ToString().ToLowerInvariant(),
            _expressions[_expressionIndex],
            _scale,
            dpi.DpiScaleX,
            milliseconds,
            Process.GetCurrentProcess().WorkingSet64));
    }
}
