using Microsoft.Win32;
using FgoPet.Core.Backup;

namespace FgoPet.App.Settings;

/// <summary>
/// Picks the local paths used by the privacy page on the host.
/// The Web surface never supplies a path, so the page cannot be turned into a generic file interface.
/// </summary>
public interface IPrivacyFilePicker
{
    /// <summary>Save dialog for the user-data export archive. Returns the chosen path, or null if cancelled.</summary>
    ValueTask<string?> PickExportDestinationAsync(CancellationToken cancellationToken);

    /// <summary>Save dialog for a private backup archive. Returns the chosen path, or null if cancelled.</summary>
    ValueTask<string?> PickBackupDestinationAsync(CancellationToken cancellationToken);

    /// <summary>Open dialog for an existing private backup archive. Returns the chosen path, or null if cancelled.</summary>
    ValueTask<string?> PickBackupSourceAsync(CancellationToken cancellationToken);
}

/// <summary>Default host picker built on the WPF dialogs.</summary>
public sealed class WpfPrivacyFilePicker : IPrivacyFilePicker
{
    private const string BackupFilter = "Fgo Pet 私有备份 (*.fgopetbackup)|*.fgopetbackup";

    public ValueTask<string?> PickExportDestinationAsync(CancellationToken cancellationToken) =>
        PickSave("ZIP 压缩包 (*.zip)|*.zip", ".zip", "fgo-pet-export.zip");

    public ValueTask<string?> PickBackupDestinationAsync(CancellationToken cancellationToken) =>
        PickSave(BackupFilter, BackupFormat.Extension, "fgo-pet-backup.fgopetbackup");

    public ValueTask<string?> PickBackupSourceAsync(CancellationToken cancellationToken)
    {
        var dialog = new OpenFileDialog
        {
            CheckFileExists = true,
            Multiselect = false,
            Filter = BackupFilter,
        };
        return new ValueTask<string?>(dialog.ShowDialog() == true ? dialog.FileName : null);
    }

    private static ValueTask<string?> PickSave(string filter, string defaultExtension, string fileName)
    {
        var dialog = new SaveFileDialog
        {
            AddExtension = true,
            DefaultExt = defaultExtension,
            Filter = filter,
            FileName = fileName,
        };
        return new ValueTask<string?>(dialog.ShowDialog() == true ? dialog.FileName : null);
    }
}
