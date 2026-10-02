using System.IO;
using System.Text.Json;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using FgoPet.App.Servants;
using FgoPet.App.Theming;
using FgoPet.Character.Settings;
using FgoPet.Core.Portraits;
using FgoPet.Core.Settings;
using FgoPet.UiSdk;

namespace FgoPet.App.Settings;

/// <summary>Owns the Web command adapter for the existing personalization settings.</summary>
public sealed class PersonalizationWebPage : ISettingsWebPage
{
    private const int MaxPreviewSourceBytes = 12 * 1024 * 1024;
    private const long MaxPreviewPixels = 8_388_608;
    private const int PreviewEdge = 256;
    private const int MaxPreviewPngBytes = 512 * 1024;
    private static readonly double[] SupportedScales = [0.50, 0.60, 0.75];
    private static readonly string[] PageCommands =
    [
        "personalization.get",
        "personalization.set",
        "personalization.reset",
        "personalization.setTheme",
    ];

    private readonly ICharacterSettingsStore _settings;
    private readonly IPortraitController? _portrait;
    private readonly ThemeService _theme;
    private readonly ServantLibraryViewModel? _library;
    private readonly object _libraryLoadGate = new();
    private Task? _libraryLoadTask;

    public PersonalizationWebPage(ICharacterSettingsStore settings, IPortraitController? portrait,
        ThemeService theme, ServantLibraryViewModel? library = null)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _portrait = portrait;
        _theme = theme ?? throw new ArgumentNullException(nameof(theme));
        _library = library;
    }

    public string SettingsPageId => nameof(SettingsSection.Personalization);

    public string ModulePath => "pages/personalization.js";

    public IReadOnlyList<string> Commands => PageCommands;

    public ISettingsWebPage CreateSession() => new PersonalizationWebPage(_settings, _portrait, _theme, _library);

    public async ValueTask<WebSurfaceCommandResult> HandleCommandAsync(WebSurfaceMessage message,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!TryReadPageId(message.Payload, out var pageId)
                || !string.Equals(pageId, SettingsPageId, StringComparison.Ordinal))
                return Failure("SETTINGS_INVALID_INPUT");

            return message.Type switch
            {
                "personalization.get" => await GetAsync(message.Payload, cancellationToken).ConfigureAwait(true),
                "personalization.set" => Set(message.Payload, cancellationToken),
                "personalization.reset" => Reset(message.Payload, cancellationToken),
                "personalization.setTheme" => SetTheme(message.Payload, cancellationToken),
                _ => Failure("SETTINGS_UNKNOWN_COMMAND"),
            };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            return Failure("SETTINGS_UNAVAILABLE");
        }
    }

    private async ValueTask<WebSurfaceCommandResult> GetAsync(JsonElement payload,
        CancellationToken cancellationToken)
    {
        if (!HasOnlyProperties(payload, "pageId"))
            return Failure("SETTINGS_INVALID_INPUT");

        await LoadLibraryIfNeededAsync(cancellationToken).ConfigureAwait(true);
        cancellationToken.ThrowIfCancellationRequested();
        lock (_settings)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var viewModel = CreateViewModel();
            cancellationToken.ThrowIfCancellationRequested();
            return Success(Snapshot(viewModel.Scale, viewModel.Topmost,
                viewModel.AutoCollapseExpandedPanel, viewModel.StatusText, viewModel.ErrorText));
        }
    }

    private WebSurfaceCommandResult Set(JsonElement payload, CancellationToken cancellationToken)
    {
        if (!HasOnlyProperties(payload, "pageId", "field", "value")
            || !payload.TryGetProperty("field", out var fieldElement)
            || fieldElement.ValueKind != JsonValueKind.String
            || fieldElement.GetString() is not { } field
            || !payload.TryGetProperty("value", out var value))
            return Failure("SETTINGS_INVALID_INPUT");

        Action<PersonalizationViewModel>? update = field switch
        {
            "scale" when value.ValueKind == JsonValueKind.Number
                && value.TryGetDouble(out var scale) && double.IsFinite(scale)
                && SupportedScales.Contains(scale) => viewModel => viewModel.Scale = scale,
            "topmost" when value.ValueKind is JsonValueKind.True or JsonValueKind.False
                => viewModel => viewModel.Topmost = value.GetBoolean(),
            "autoCollapseExpandedPanel" when value.ValueKind is JsonValueKind.True or JsonValueKind.False
                => viewModel => viewModel.AutoCollapseExpandedPanel = value.GetBoolean(),
            _ => null,
        };
        if (update is null)
            return Failure("SETTINGS_INVALID_INPUT");

        lock (_settings)
        {
            var viewModel = CreateViewModel();
            cancellationToken.ThrowIfCancellationRequested();
            update(viewModel);
            return SnapshotAfterWrite(viewModel);
        }
    }

    private WebSurfaceCommandResult Reset(JsonElement payload, CancellationToken cancellationToken)
    {
        if (!HasOnlyProperties(payload, "pageId"))
            return Failure("SETTINGS_INVALID_INPUT");

        lock (_settings)
        {
            var viewModel = CreateViewModel();
            cancellationToken.ThrowIfCancellationRequested();
            viewModel.ResetCommand.Execute(null);
            return SnapshotAfterWrite(viewModel);
        }
    }

    private WebSurfaceCommandResult SetTheme(JsonElement payload, CancellationToken cancellationToken)
    {
        if (!HasOnlyProperties(payload, "pageId", "theme")
            || !payload.TryGetProperty("theme", out var themeElement)
            || themeElement.ValueKind != JsonValueKind.String)
            return Failure("SETTINGS_INVALID_INPUT");

        var theme = themeElement.GetString() switch
        {
            "System" => AppTheme.System,
            "FgoLight" => AppTheme.FgoLight,
            "FgoDark" => AppTheme.FgoDark,
            _ => (AppTheme?)null,
        };
        if (theme is null)
            return Failure("SETTINGS_INVALID_INPUT");

        cancellationToken.ThrowIfCancellationRequested();
        _theme.Select(theme.Value);
        cancellationToken.ThrowIfCancellationRequested();
        lock (_settings)
        {
            var persisted = _settings.Load();
            return Success(Snapshot(persisted.Scale, persisted.Topmost,
                persisted.AutoCollapseExpandedPanel, string.Empty, string.Empty));
        }
    }

    private WebSurfaceCommandResult SnapshotAfterWrite(PersonalizationViewModel viewModel)
    {
        if (!string.IsNullOrEmpty(viewModel.ErrorText))
        {
            try
            {
                var failureSnapshot = _settings.Load();
                return new WebSurfaceCommandResult(false,
                    new { snapshot = Snapshot(failureSnapshot.Scale, failureSnapshot.Topmost,
                        failureSnapshot.AutoCollapseExpandedPanel, "保存失败", viewModel.ErrorText) },
                    "SETTINGS_SAVE_FAILED");
            }
            catch (Exception)
            {
                return Failure("SETTINGS_UNAVAILABLE");
            }
        }

        var persisted = _settings.Load();
        return Success(Snapshot(persisted.Scale, persisted.Topmost,
            persisted.AutoCollapseExpandedPanel, viewModel.StatusText, viewModel.ErrorText));
    }

    private object Snapshot(double scale, bool topmost, bool autoCollapseExpandedPanel,
        string statusText, string errorText)
    {
        var selectedTheme = ThemeName(_theme.SelectedTheme);
        var effectiveTheme = ThemeName(_theme.CurrentTheme);
        var servant = _library?.SelectedServant;
        var currentRole = servant is null
            ? null
            : new
            {
                displayName = servant.DisplayName,
                previewDataUrl = TryCreatePreviewDataUrl(servant.PreviewSource),
            };

        return new
        {
            scale = IsSupportedScale(scale) ? scale : CharacterSettings.Defaults.Scale,
            scaleOptions = SupportedScales,
            topmost,
            autoCollapseExpandedPanel,
            statusText,
            errorText,
            currentRole,
            theme = new
            {
                selected = selectedTheme,
                effective = effectiveTheme,
                statusText = _theme.StatusText,
            },
        };
    }

    private async Task LoadLibraryIfNeededAsync(CancellationToken cancellationToken)
    {
        if (_library is null)
            return;

        Task? loadTask;
        lock (_libraryLoadGate)
        {
            if (_libraryLoadTask is null && _library.Servants.Count == 0)
                _libraryLoadTask = _library.LoadAsync();
            loadTask = _libraryLoadTask;
        }

        if (loadTask is not null)
            await loadTask.WaitAsync(cancellationToken).ConfigureAwait(true);
    }

    private PersonalizationViewModel CreateViewModel() => new(_settings, _portrait);

    private static bool IsSupportedScale(double scale) => double.IsFinite(scale) && SupportedScales.Contains(scale);

    internal static string? TryCreatePreviewDataUrl(string? previewPath)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(previewPath) || !Path.IsPathFullyQualified(previewPath)
                || previewPath.StartsWith("\\\\", StringComparison.Ordinal))
                return null;

            var file = new FileInfo(previewPath);
            if (!file.Exists || file.Length <= 0 || file.Length > MaxPreviewSourceBytes)
                return null;

            using var stream = new FileStream(file.FullName, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (stream.Length <= 0 || stream.Length > MaxPreviewSourceBytes)
                return null;

            var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat,
                BitmapCacheOption.OnDemand);
            var frame = decoder.Frames.FirstOrDefault();
            if (frame is null || frame.PixelWidth <= 0 || frame.PixelHeight <= 0
                || (long)frame.PixelWidth * frame.PixelHeight > MaxPreviewPixels)
                return null;

            var scale = Math.Min((double)PreviewEdge / frame.PixelWidth, (double)PreviewEdge / frame.PixelHeight);
            if (scale > 1d)
                scale = 1d;
            BitmapSource preview = scale < 1d
                ? new TransformedBitmap(frame, new ScaleTransform(scale, scale))
                : frame;
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(preview));
            using var output = new MemoryStream();
            encoder.Save(output);
            if (output.Length > MaxPreviewPngBytes)
                return null;

            return "data:image/png;base64," + Convert.ToBase64String(output.GetBuffer(), 0, (int)output.Length);
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static bool TryReadPageId(JsonElement payload, out string pageId)
    {
        pageId = string.Empty;
        return payload.ValueKind == JsonValueKind.Object
            && payload.TryGetProperty("pageId", out var element)
            && element.ValueKind == JsonValueKind.String
            && (pageId = element.GetString() ?? string.Empty).Length > 0;
    }

    private static bool HasOnlyProperties(JsonElement payload, params string[] allowedNames)
    {
        if (payload.ValueKind != JsonValueKind.Object)
            return false;
        var allowed = new HashSet<string>(allowedNames, StringComparer.Ordinal);
        return payload.EnumerateObject().All(property => allowed.Contains(property.Name))
            && allowedNames.All(name => payload.TryGetProperty(name, out _));
    }

    private static string ThemeName(AppTheme theme) => theme switch
    {
        AppTheme.System => "System",
        AppTheme.FgoLight => "FgoLight",
        AppTheme.FgoDark => "FgoDark",
        _ => "FgoLight",
    };

    private static WebSurfaceCommandResult Success(object payload) => new(true, payload);

    private static WebSurfaceCommandResult Failure(string errorCode) => new(false, ErrorCode: errorCode);
}
