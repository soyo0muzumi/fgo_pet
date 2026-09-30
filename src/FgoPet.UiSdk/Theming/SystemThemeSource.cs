using FgoPet.Core.Settings;
using Microsoft.Win32;

namespace FgoPet.App.Theming;

public interface ISystemThemeSource
{
    event EventHandler? Changed;
    AppTheme ReadTheme();
}

/// <summary>Observes the Windows application color preference without retaining any user data.</summary>
public sealed class WindowsSystemThemeSource : ISystemThemeSource, IDisposable
{
    private bool _disposed;

    public WindowsSystemThemeSource() => SystemEvents.UserPreferenceChanged += OnPreferenceChanged;

    public event EventHandler? Changed;

    public AppTheme ReadTheme()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme") is int value && value == 0
                ? AppTheme.FgoDark : AppTheme.FgoLight;
        }
        catch (Exception) { return AppTheme.FgoLight; }
    }

    private void OnPreferenceChanged(object sender, UserPreferenceChangedEventArgs e)
    {
        if (!_disposed) Changed?.Invoke(this, EventArgs.Empty);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        SystemEvents.UserPreferenceChanged -= OnPreferenceChanged;
    }
}
