using System.Text.Json;
using FgoPet.App.Memory;
using FgoPet.UiSdk;

namespace FgoPet.App.Settings;

/// <summary>Owns the Web command adapter for the existing privacy settings page.</summary>
/// <remarks>
/// Owns the data-management presentation: the export and delete-all commands reuse the shared
/// <see cref="MemoryViewModel"/> exactly like the WPF page, while private backup and restore are
/// injected as delegates so the adapter can be exercised without a real backup service.
/// Restore is the highest-risk path: the archive is always chosen through the host picker
/// (<see cref="IPrivacyFilePicker"/>), the Web payload never carries a path, and preparation still
/// ends by requesting the normal exit the app needs to apply the pending restore.
/// The view model exposes a single <c>StatusText</c> shared by every command; the Web page keeps one
/// status line per card instead, so each card only reports what its own action produced. That also
/// keeps the export success message, the one place the view model embeds an absolute path, from
/// reaching the surface: it is replaced with a path-free sentence.
/// </remarks>
public sealed class PrivacyWebPage : ISettingsWebPage
{
    private static readonly string[] PageCommands =
    [
        "privacy.get",
        "privacy.export",
        "privacy.createBackup",
        "privacy.restoreBackup",
        "privacy.deleteAll",
    ];

    private readonly MemoryViewModel _viewModel;
    private readonly IPrivacyFilePicker _picker;
    private readonly bool _exportAvailable;
    private readonly Func<string, CancellationToken, Task>? _createBackup;
    private readonly Func<string, CancellationToken, Task>? _prepareRestore;
    private readonly Action? _exitForRestore;

    private string _exportStatusText = string.Empty;
    private string _backupStatusText = string.Empty;
    private string _deleteStatusText = string.Empty;

    public PrivacyWebPage(
        MemoryViewModel viewModel,
        IPrivacyFilePicker picker,
        bool exportAvailable = true,
        Func<string, CancellationToken, Task>? createBackup = null,
        Func<string, CancellationToken, Task>? prepareRestore = null,
        Action? exitForRestore = null)
    {
        _viewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
        _picker = picker ?? throw new ArgumentNullException(nameof(picker));
        _exportAvailable = exportAvailable;
        _createBackup = createBackup;
        _prepareRestore = prepareRestore;
        _exitForRestore = exitForRestore;
    }

    public string SettingsPageId => "Privacy";

    public string ModulePath => "pages/privacy.js";

    public IReadOnlyList<string> Commands => PageCommands;

    public ISettingsWebPage CreateSession() => new PrivacyWebPage(
        _viewModel, _picker, _exportAvailable, _createBackup, _prepareRestore, _exitForRestore);

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
            if (!HasOnlyProperties(message.Payload, "pageId"))
                return Failure("SETTINGS_INVALID_INPUT");

            return message.Type switch
            {
                "privacy.get" => Success(Snapshot()),
                "privacy.export" => await ExportAsync(cancellationToken).ConfigureAwait(true),
                "privacy.createBackup" => await CreateBackupAsync(cancellationToken).ConfigureAwait(true),
                "privacy.restoreBackup" => await RestoreBackupAsync(cancellationToken).ConfigureAwait(true),
                "privacy.deleteAll" => await DeleteAllAsync(cancellationToken).ConfigureAwait(true),
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

    private async ValueTask<WebSurfaceCommandResult> ExportAsync(CancellationToken cancellationToken)
    {
        // 能力缺失（导出器未注册）是「这一页没有这个功能」，不是「操作失败」：
        // 与备份/恢复一致，用状态文案表达并让按钮置灰，不抛错误码，也不弹选择器。
        if (!_exportAvailable)
        {
            _exportStatusText = "导出服务不可用。";
            return Success(Snapshot());
        }

        var destination = await _picker.PickExportDestinationAsync(cancellationToken).ConfigureAwait(true);
        cancellationToken.ThrowIfCancellationRequested();
        if (destination is null)
        {
            // 取消选择不改变任何状态，也不写状态文案（WPF 页同样直接 return）。
            return Success(Snapshot());
        }

        // The view model persists the path and performs the export; the Web never sees either.
        _viewModel.ExportPath = destination;
        await _viewModel.ExportCommand.ExecuteAsync(null).ConfigureAwait(true);
        cancellationToken.ThrowIfCancellationRequested();
        _exportStatusText = PathFreeStatus();
        return Success(Snapshot());
    }

    private async ValueTask<WebSurfaceCommandResult> CreateBackupAsync(CancellationToken cancellationToken)
    {
        if (_createBackup is null)
        {
            _backupStatusText = "私有备份服务不可用。";
            return Success(Snapshot());
        }

        var destination = await _picker.PickBackupDestinationAsync(cancellationToken).ConfigureAwait(true);
        cancellationToken.ThrowIfCancellationRequested();
        if (destination is null)
            return Success(Snapshot());

        try
        {
            await _createBackup(destination, cancellationToken).ConfigureAwait(true);
            cancellationToken.ThrowIfCancellationRequested();
            _backupStatusText = "私有备份已创建。";
            return Success(Snapshot());
        }
        catch (Core.Backup.BackupException error)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _backupStatusText = $"私有备份失败：{error.Code}";
            return Failure("SETTINGS_SAVE_FAILED");
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            _backupStatusText = "私有备份失败：操作未完成。";
            return Failure("SETTINGS_SAVE_FAILED");
        }
    }

    private async ValueTask<WebSurfaceCommandResult> RestoreBackupAsync(CancellationToken cancellationToken)
    {
        if (_prepareRestore is null)
        {
            _backupStatusText = "私有恢复服务不可用。";
            return Success(Snapshot());
        }

        var source = await _picker.PickBackupSourceAsync(cancellationToken).ConfigureAwait(true);
        cancellationToken.ThrowIfCancellationRequested();
        if (source is null)
            return Success(Snapshot());

        try
        {
            await _prepareRestore(source, cancellationToken).ConfigureAwait(true);
            cancellationToken.ThrowIfCancellationRequested();
            _backupStatusText = "备份已校验，请重新打开应用以完成恢复。";
            // 与 WPF 页一致：准备完成后立即请求正常退出（恢复在下次启动时应用）。
            _exitForRestore?.Invoke();
            return Success(Snapshot());
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            _backupStatusText = "恢复失败：操作未完成。";
            return Failure("SETTINGS_RESTORE_FAILED");
        }
    }

    private async ValueTask<WebSurfaceCommandResult> DeleteAllAsync(CancellationToken cancellationToken)
    {
        await _viewModel.DeleteAllCommand.ExecuteAsync(null).ConfigureAwait(true);
        cancellationToken.ThrowIfCancellationRequested();
        _deleteStatusText = _viewModel.StatusText;
        return Success(Snapshot());
    }

    private object Snapshot() => new
    {
        exportStatusText = _exportStatusText,
        backupStatusText = _backupStatusText,
        deleteStatusText = _deleteStatusText,
        canExport = _exportAvailable,
        canCreateBackup = _createBackup is not null,
        canRestoreBackup = _prepareRestore is not null,
        isBusy = _viewModel.IsLoading,
        isLoading = _viewModel.IsLoading,
    };

    /// <summary>
    /// The view model's status text is shared by export and delete-all, like the WPF page.
    /// Export success is the only message that embeds an absolute path, and the Web surface must not
    /// carry local paths, so that one message is replaced by a path-free equivalent.
    /// </summary>
    private string PathFreeStatus()
    {
        var text = _viewModel.StatusText;
        return text.StartsWith("已导出", StringComparison.Ordinal) ? "已导出到所选文件。" : text;
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

    private static WebSurfaceCommandResult Success(object payload) => new(true, payload);

    private static WebSurfaceCommandResult Failure(string errorCode) => new(false, ErrorCode: errorCode);
}
