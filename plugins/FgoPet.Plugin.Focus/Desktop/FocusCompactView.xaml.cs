using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using FgoPet.UiSdk;

namespace FgoPet.Plugin.Focus.Desktop;

public partial class FocusCompactView : UserControl, ICompactSurfaceView
{
    private FocusCompactViewModel? _subscribed;
    private bool _expanded;
    private bool _options;
    private bool _hover;
    private bool _peekPinned;
    private bool _keyboardReveal;
    private bool _peekDismissed;
    private bool _wasActive;
    private bool _disposed;
    private Window? _ownerWindow;
    private FocusCompactViewModel? Model => DataContext as FocusCompactViewModel;
    public FocusCompactView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => Attach();
        Loaded += (_, _) => Attach();
        Unloaded += (_, _) => Detach();
    }
    public void SetExpanded(bool expanded)
    {
        if (expanded && !_expanded && Model?.IsActive == true) { _peekPinned = true; _peekDismissed = false; }
        if (!expanded && _expanded) { _peekPinned = false; _keyboardReveal = false; _hover = false; }
        _expanded = expanded;
        ApplyState();
    }
    private void Attach()
    {
        Detach();
        if (_disposed) return;
        _subscribed = Model;
        if (_subscribed is not null) _subscribed.PropertyChanged += OnChanged;
        _ownerWindow = Window.GetWindow(this);
        if (_ownerWindow is not null)
        {
            _ownerWindow.PreviewMouseDown += OnOwnerMouseDown;
            _ownerWindow.Deactivated += OnOwnerDeactivated;
        }
        ApplyState();
    }
    private void Detach()
    {
        if (_subscribed is not null) _subscribed.PropertyChanged -= OnChanged;
        _subscribed = null;
        if (_ownerWindow is not null)
        {
            _ownerWindow.PreviewMouseDown -= OnOwnerMouseDown;
            _ownerWindow.Deactivated -= OnOwnerDeactivated;
            _ownerWindow = null;
        }
    }
    public void Dispose() { _disposed = true; Detach(); }
    private void DismissPeek()
    {
        _peekDismissed = true;
        _peekPinned = false;
        _keyboardReveal = false;
        _hover = false;
        ApplyState();
    }
    private void OnOwnerDeactivated(object? sender, EventArgs e) => DismissPeek();
    private void OnOwnerMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (Model?.IsActive != true) return;
        if (e.OriginalSource is DependencyObject source
            && (ReferenceEquals(source, FocusPeek) || FocusPeek.IsAncestorOf(source)
                || ReferenceEquals(source, RibbonButton) || RibbonButton.IsAncestorOf(source))) return;
        DismissPeek();
    }
    private void OnChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName is nameof(FocusCompactViewModel.IsActive) or nameof(FocusCompactViewModel.CanPause)
            or nameof(FocusCompactViewModel.CanStartFocus)) ApplyState();
    }
    private void ApplyState()
    {
        var active = Model?.IsActive == true;
        if (active && !_wasActive) { _peekPinned = false; _keyboardReveal = false; _peekDismissed = false; _options = false; }
        _wasActive = active;
        FocusSetupCard.Visibility = _expanded && !active ? Visibility.Visible : Visibility.Collapsed;
        QuickDurations.Visibility = _options ? Visibility.Collapsed : Visibility.Visible;
        SessionOptions.Visibility = _options ? Visibility.Visible : Visibility.Collapsed;
        CompactTimer.Visibility = active ? Visibility.Visible : Visibility.Collapsed;
        FocusPeek.Visibility = active && !_peekDismissed && (_hover || _peekPinned || _keyboardReveal) ? Visibility.Visible : Visibility.Collapsed;
    }
    private void OnQuickStart(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string text } && int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var minutes))
            Model?.StartQuickFocus(minutes);
    }
    private void OnSessionOptions(object sender, RoutedEventArgs e) { _options = true; Model?.SelectCustomPreset(); ApplyState(); }
    private void OnSessionBack(object sender, RoutedEventArgs e) { _options = false; ApplyState(); }
    private void OnStartFocusClick(object sender, RoutedEventArgs e) => Model?.StartFocus();
    private void OnPauseResumeClick(object sender, RoutedEventArgs e)
    {
        if (Model?.CanPause == true) Model.PauseTimer();
        else if (Model?.CanResume == true) Model.ResumeTimer();
    }
    private void OnStopTimerClick(object sender, RoutedEventArgs e) => Model?.StopTimer();
    private void OnRibbonClick(object sender, RoutedEventArgs e)
    {
        _peekDismissed = FocusPeek.Visibility == Visibility.Visible;
        _peekPinned = !_peekDismissed;
        _keyboardReveal = false;
        ApplyState();
    }
    private void OnPeekEnter(object sender, MouseEventArgs e) { _peekDismissed = false; _hover = true; ApplyState(); }
    private void OnPeekLeave(object sender, MouseEventArgs e) { _peekDismissed = false; _hover = false; ApplyState(); }
    private void OnPeekFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        _keyboardReveal = e.NewFocus is not null && IsKeyboardFocusWithin && Mouse.LeftButton != MouseButtonState.Pressed;
        Dispatcher.BeginInvoke(ApplyState);
    }
}
