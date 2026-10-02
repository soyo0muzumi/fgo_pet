using System.IO;
using System.Text.Json;
using FgoPet.App.Theming;
using FgoPet.Character.Settings;
using FgoPet.UiSdk;

namespace FgoPet.App.Settings;

/// <summary>The user-profile Settings page, backed by the existing profile owner.</summary>
public sealed class UserProfileWebFactory : ISettingsWebPage
{
    private static readonly string[] PageCommands = ["getProfile", "saveProfile", "resetProfile"];
    private static readonly WebSurfacePolicy Policy = new(
        "https://settings.fgopet.invalid/index.html",
        PageCommands);
    private readonly ICharacterSettingsStore _settings;
    private readonly Func<UserProfileViewModel> _createViewModel;
    private readonly string _storageRoot;
    private readonly ThemeService? _theme;
    private readonly object _viewModelGate = new();
    private UserProfileViewModel? _viewModel;

    public UserProfileWebFactory(ICharacterSettingsStore settings, UserProfileViewModel viewModel,
        string storageRoot, ThemeService? theme = null)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _viewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
        _createViewModel = () => viewModel;
        _storageRoot = storageRoot ?? throw new ArgumentNullException(nameof(storageRoot));
        _theme = theme;
    }

    private UserProfileWebFactory(ICharacterSettingsStore settings,
        Func<UserProfileViewModel> createViewModel, string storageRoot, ThemeService? theme)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _createViewModel = createViewModel ?? throw new ArgumentNullException(nameof(createViewModel));
        _storageRoot = storageRoot ?? throw new ArgumentNullException(nameof(storageRoot));
        _theme = theme;
    }

    public string SettingsPageId => nameof(SettingsSection.UserProfile);
    public string ModulePath => "pages/user-profile.js";
    public IReadOnlyList<string> Commands => PageCommands;

    public WebView2SurfaceHost CreateView()
    {
        var session = (UserProfileWebFactory)CreateSession();
        return session.CreateViewForCurrentOwner();
    }

    public ISettingsWebPage CreateSession() => new UserProfileWebFactory(_settings,
        () => new UserProfileViewModel(_settings), _storageRoot, _theme);

    private WebView2SurfaceHost CreateViewForCurrentOwner()
    {
        var host = new WebView2SurfaceHost(Policy,
            Path.Combine(AppContext.BaseDirectory, "Desktop", "ui", "settings", "user-profile"),
            Path.Combine(_storageRoot, "WebView2", "settings", "user-profile"), HandleCommandAsync)
        { MinHeight = 380 };
        if (_theme is not null)
        {
            var version = 1L;
            EventHandler changed = (_, _) => host.SetThemeVersion(
                Interlocked.Increment(ref version), _theme.CaptureWebPalette());
            _theme.ThemeChanged += changed;
            host.Disposed += (_, _) => _theme.ThemeChanged -= changed;
            host.SetThemeVersion(version, _theme.CaptureWebPalette());
        }
        return host;
    }

    internal ValueTask<WebSurfaceCommandResult> HandleCommandAsync(WebSurfaceMessage message,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            return ValueTask.FromResult(message.Type switch
            {
                "getProfile" => new WebSurfaceCommandResult(true, Snapshot()),
                "saveProfile" => Save(message.Payload),
                "resetProfile" => Reset(),
                _ => new WebSurfaceCommandResult(false, ErrorCode: "SETTINGS_UNKNOWN_COMMAND"),
            });
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            return ValueTask.FromResult(new WebSurfaceCommandResult(false, ErrorCode: "SETTINGS_UNAVAILABLE"));
        }
    }

    ValueTask<WebSurfaceCommandResult> ISettingsWebPage.HandleCommandAsync(WebSurfaceMessage message,
        CancellationToken cancellationToken) => HandleCommandAsync(message, cancellationToken);

    private object Snapshot()
    {
        lock (_settings)
        {
            return new
            {
                displayName = _settings.Load().UserProfile?.DisplayName ?? string.Empty,
                explanation = ViewModel.ProfileOnlyExplanation,
            };
        }
    }

    private WebSurfaceCommandResult Save(JsonElement payload)
    {
        if (payload.ValueKind != JsonValueKind.Object
            || !payload.TryGetProperty("displayName", out var field)
            || field.ValueKind != JsonValueKind.String || field.GetString() is not { } name
            || name.Length > 80)
            return new(false, ErrorCode: "SETTINGS_INVALID_INPUT");
        lock (_settings)
        {
            ViewModel.DisplayName = name;
            ViewModel.SaveCommand.Execute(null);
            return string.IsNullOrEmpty(ViewModel.ErrorText)
                ? new(true, Snapshot())
                : new(false, new { message = ViewModel.ErrorText }, "SETTINGS_SAVE_FAILED");
        }
    }

    private WebSurfaceCommandResult Reset()
    {
        lock (_settings)
        {
            ViewModel.ResetCommand.Execute(null);
            return string.IsNullOrEmpty(ViewModel.ErrorText)
                ? new(true, Snapshot())
                : new(false, new { message = ViewModel.ErrorText }, "SETTINGS_SAVE_FAILED");
        }
    }

    private UserProfileViewModel ViewModel
    {
        get
        {
            lock (_viewModelGate)
                return _viewModel ??= _createViewModel();
        }
    }
}
