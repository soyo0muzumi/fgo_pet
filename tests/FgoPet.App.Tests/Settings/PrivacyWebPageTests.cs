using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using FgoPet.App.Memory;
using FgoPet.App.Settings;
using FgoPet.Core.Backup;
using FgoPet.Core.Memory;
using FgoPet.Infrastructure.Memory;
using FgoPet.Infrastructure.Persistence;
using FgoPet.App.Privacy;
using FgoPet.UiSdk;
using Xunit;

namespace FgoPet.App.Tests.Settings;

public sealed class PrivacyWebPageTests : IDisposable
{
    private const string PageId = "Privacy";

    private readonly string _path = Path.Combine(Path.GetTempPath(), $"fgo-privacy-web-{Guid.NewGuid():N}.db");
    private readonly RuntimeDatabase _database;
    private readonly MemoryCandidateService _memories;

    public PrivacyWebPageTests()
    {
        _database = TestRuntimeDatabase.Create(_path);
        new RuntimeDatabaseMigrator(_database).Migrate();
        _memories = new MemoryCandidateService(new SqliteMemoryRepository(_database), TimeProvider.System);
    }

    public void Dispose()
    {
        if (File.Exists(_path))
        {
            try { File.Delete(_path); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    [Fact]
    public async Task Get_reports_service_availability_without_touching_the_disk()
    {
        var picker = new RecordingPicker();
        var page = new PrivacyWebPage(new MemoryViewModel(_memories), picker);

        var snapshot = await ReadAsync(page, "privacy.get");

        Assert.True(snapshot.GetProperty("canExport").GetBoolean());
        Assert.False(snapshot.GetProperty("canCreateBackup").GetBoolean());
        Assert.False(snapshot.GetProperty("canRestoreBackup").GetBoolean());
        Assert.Equal(string.Empty, snapshot.GetProperty("exportStatusText").GetString());
        Assert.Equal(string.Empty, snapshot.GetProperty("backupStatusText").GetString());
        Assert.Equal(string.Empty, snapshot.GetProperty("deleteStatusText").GetString());
        Assert.Empty(picker.Calls);
    }

    [Fact]
    public async Task Export_passes_the_picked_path_to_the_exporter_but_never_reports_it_back()
    {
        var picker = new RecordingPicker { ExportDestination = @"C:\Users\tester\Documents\export.zip" };
        var exporter = new RecordingExporter();
        var page = new PrivacyWebPage(new MemoryViewModel(_memories, exporter), picker);

        var snapshot = await ReadAsync(page, "privacy.export");

        Assert.Equal(new[] { @"C:\Users\tester\Documents\export.zip" }, exporter.Destinations);
        Assert.Equal("已导出到所选文件。", snapshot.GetProperty("exportStatusText").GetString());
        Assert.DoesNotContain(@"C:\", JsonSerializer.Serialize(snapshot), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Export_reports_a_missing_exporter_without_calling_the_picker()
    {
        var picker = new RecordingPicker { ExportDestination = @"C:\export.zip" };
        var exporter = new RecordingExporter();
        var page = new PrivacyWebPage(new MemoryViewModel(_memories, exporter), picker, exportAvailable: false);

        var snapshot = await ReadAsync(page, "privacy.export");

        Assert.False(snapshot.GetProperty("canExport").GetBoolean());
        Assert.Equal("导出服务不可用。", snapshot.GetProperty("exportStatusText").GetString());
        // 能力缺失时不该弹出选择器，也不该调用导出器：没有位置可选，也没有东西会被写出。
        Assert.Empty(picker.Calls);
        Assert.Empty(exporter.Destinations);
    }

    [Fact]
    public async Task Cancelling_the_export_picker_leaves_every_status_untouched()
    {
        var picker = new RecordingPicker { ExportDestination = null };
        var exporter = new RecordingExporter();
        var page = new PrivacyWebPage(new MemoryViewModel(_memories, exporter), picker);

        var snapshot = await ReadAsync(page, "privacy.export");

        Assert.Empty(exporter.Destinations);
        Assert.Equal(string.Empty, snapshot.GetProperty("exportStatusText").GetString());
    }

    [Fact]
    public async Task Creating_a_backup_uses_the_host_picker_and_reports_success()
    {
        var picker = new RecordingPicker { BackupDestination = @"C:\backups\one.fgopetbackup" };
        var received = new List<string>();
        var page = new PrivacyWebPage(new MemoryViewModel(_memories), picker,
            createBackup: (path, _) => { received.Add(path); return Task.CompletedTask; });

        var snapshot = await ReadAsync(page, "privacy.createBackup");

        Assert.Equal(new[] { @"C:\backups\one.fgopetbackup" }, received);
        Assert.Equal("私有备份已创建。", snapshot.GetProperty("backupStatusText").GetString());
        Assert.True(snapshot.GetProperty("canCreateBackup").GetBoolean());
    }

    [Fact]
    public async Task Creating_a_backup_without_a_service_reports_unavailable_and_skips_the_picker()
    {
        var picker = new RecordingPicker { BackupDestination = @"C:\backups\one.fgopetbackup" };
        var page = new PrivacyWebPage(new MemoryViewModel(_memories), picker);

        var snapshot = await ReadAsync(page, "privacy.createBackup");

        Assert.False(snapshot.GetProperty("canCreateBackup").GetBoolean());
        Assert.Equal("私有备份服务不可用。", snapshot.GetProperty("backupStatusText").GetString());
        Assert.Empty(picker.Calls);
    }

    [Fact]
    public async Task Restoring_without_a_service_reports_unavailable_and_skips_the_picker()
    {
        var picker = new RecordingPicker { BackupSource = @"C:\backups\one.fgopetbackup" };
        var exits = 0;
        var page = new PrivacyWebPage(new MemoryViewModel(_memories), picker, exitForRestore: () => exits++);

        var snapshot = await ReadAsync(page, "privacy.restoreBackup");

        Assert.False(snapshot.GetProperty("canRestoreBackup").GetBoolean());
        Assert.Equal("私有恢复服务不可用。", snapshot.GetProperty("backupStatusText").GetString());
        Assert.Empty(picker.Calls);
        Assert.Equal(0, exits);
    }

    [Fact]
    public async Task Creating_a_backup_surfaces_a_backup_failure_code()
    {
        var picker = new RecordingPicker { BackupDestination = @"C:\backups\one.fgopetbackup" };
        var page = new PrivacyWebPage(new MemoryViewModel(_memories), picker,
            createBackup: (_, _) => throw new BackupException(
                BackupFailureCode.SwapFailed, "boom"));

        var result = await SendAsync(page, "privacy.createBackup");
        var snapshot = await ReadAsync(page, "privacy.get");

        Assert.False(result.Success);
        Assert.Equal("SETTINGS_SAVE_FAILED", result.ErrorCode);
        Assert.StartsWith("私有备份失败：", snapshot.GetProperty("backupStatusText").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Restoring_a_backup_prepares_it_and_requests_the_normal_exit()
    {
        var picker = new RecordingPicker { BackupSource = @"C:\backups\one.fgopetbackup" };
        var received = new List<string>();
        var exits = 0;
        var page = new PrivacyWebPage(new MemoryViewModel(_memories), picker,
            prepareRestore: (path, _) => { received.Add(path); return Task.CompletedTask; },
            exitForRestore: () => exits++);

        var snapshot = await ReadAsync(page, "privacy.restoreBackup");

        Assert.Equal([@"C:\backups\one.fgopetbackup"], received);
        Assert.Equal(1, exits);
        Assert.Equal("备份已校验，请重新打开应用以完成恢复。", snapshot.GetProperty("backupStatusText").GetString());
    }

    [Fact]
    public async Task Restore_failure_does_not_exit_the_application()
    {
        var picker = new RecordingPicker { BackupSource = @"C:\backups\one.fgopetbackup" };
        var exits = 0;
        var page = new PrivacyWebPage(new MemoryViewModel(_memories), picker,
            prepareRestore: (_, _) => throw new InvalidOperationException("bad archive"),
            exitForRestore: () => exits++);

        var result = await SendAsync(page, "privacy.restoreBackup");
        var snapshot = await ReadAsync(page, "privacy.get");

        Assert.False(result.Success);
        Assert.Equal("SETTINGS_RESTORE_FAILED", result.ErrorCode);
        Assert.Equal(0, exits);
        Assert.Equal("恢复失败：操作未完成。", snapshot.GetProperty("backupStatusText").GetString());
    }

    [Fact]
    public async Task Delete_all_reports_the_view_model_status_and_ignores_the_memory_page_status()
    {
        var deleter = new RecordingDeleter();
        var page = new PrivacyWebPage(new MemoryViewModel(_memories, deletion: deleter), new RecordingPicker());

        var before = await ReadAsync(page, "privacy.get");
        // 隐私页不显示记忆页的状态：初始状态必须是空的，而不是「请先选择角色。」。
        Assert.Equal(string.Empty, before.GetProperty("deleteStatusText").GetString());

        var snapshot = await ReadAsync(page, "privacy.deleteAll");

        Assert.Equal(1, deleter.DeleteAllCalls);
        Assert.Equal("已删除全部用户数据（不含 Phase 2 专注/羁绊历史）。",
            snapshot.GetProperty("deleteStatusText").GetString());
    }

    [Fact]
    public async Task Delete_all_reports_an_unavailable_service_through_its_own_status_line()
    {
        var page = new PrivacyWebPage(new MemoryViewModel(_memories), new RecordingPicker());

        var snapshot = await ReadAsync(page, "privacy.deleteAll");

        Assert.Equal("删除服务不可用。", snapshot.GetProperty("deleteStatusText").GetString());
    }

    [Fact]
    public async Task Unknown_command_and_wrong_page_id_are_rejected()
    {
        var page = new PrivacyWebPage(new MemoryViewModel(_memories), new RecordingPicker());

        var unknown = await SendAsync(page, "privacy.nope", $"{{\"pageId\":\"{PageId}\"}}");
        Assert.False(unknown.Success);
        Assert.Equal("SETTINGS_UNKNOWN_COMMAND", unknown.ErrorCode);

        var missing = await SendAsync(page, "privacy.get", "{}");
        Assert.False(missing.Success);
        Assert.Equal("SETTINGS_INVALID_INPUT", missing.ErrorCode);

        var mismatched = await SendAsync(page, "privacy.get", "{\"pageId\":\"ConversationMemory\"}");
        Assert.False(mismatched.Success);
        Assert.Equal("SETTINGS_INVALID_INPUT", mismatched.ErrorCode);
    }

    [Fact]
    public async Task The_web_surface_can_never_supply_a_path()
    {
        var picker = new RecordingPicker { ExportDestination = @"C:\export.zip" };
        var page = new PrivacyWebPage(new MemoryViewModel(_memories, new RecordingExporter()), picker);

        // 任何试图自带路径的载荷都必须被严格白名单拒绝。
        var withPath = await SendAsync(page, "privacy.export", $"{{\"pageId\":\"{PageId}\",\"path\":\"C:\\\\evil.zip\"}}");
        Assert.False(withPath.Success);
        Assert.Equal("SETTINGS_INVALID_INPUT", withPath.ErrorCode);

        var withDestination = await SendAsync(page, "privacy.createBackup",
            $"{{\"pageId\":\"{PageId}\",\"destinationPath\":\"C:\\\\evil.fgopetbackup\"}}");
        Assert.False(withDestination.Success);
        Assert.Equal("SETTINGS_INVALID_INPUT", withDestination.ErrorCode);

        Assert.Empty(picker.Calls);
    }

    [Fact]
    public async Task Sessions_are_independent_and_reset_their_own_status()
    {
        var picker = new RecordingPicker { BackupDestination = @"C:\backups\one.fgopetbackup" };
        var first = new PrivacyWebPage(new MemoryViewModel(_memories), picker,
            createBackup: (_, _) => Task.CompletedTask);
        await ReadAsync(first, "privacy.createBackup");

        var second = first.CreateSession();
        var snapshot = await ReadAsync(second, "privacy.get");

        Assert.Equal(string.Empty, snapshot.GetProperty("backupStatusText").GetString());
    }

    [Fact]
    public async Task Cancelled_token_is_not_swallowed_into_a_failure()
    {
        var page = new PrivacyWebPage(new MemoryViewModel(_memories), new RecordingPicker { ExportDestination = null });
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        var payload = JsonDocument.Parse($"{{\"pageId\":\"{PageId}\"}}").RootElement.Clone();
        await Assert.ThrowsAsync<OperationCanceledException>(
            () => page.HandleCommandAsync(new WebSurfaceMessage("privacy.get", null, payload), cancellation.Token)
                .AsTask());
    }

    private static async Task<WebSurfaceCommandResult> SendAsync(ISettingsWebPage page, string command, string payload)
    {
        using var document = JsonDocument.Parse(payload);
        return await page.HandleCommandAsync(
            new WebSurfaceMessage(command, null, document.RootElement.Clone()), CancellationToken.None);
    }

    private static async Task<WebSurfaceCommandResult> SendAsync(ISettingsWebPage page, string command) =>
        await SendAsync(page, command, $"{{\"pageId\":\"{PageId}\"}}");

    private static async Task<JsonElement> ReadAsync(ISettingsWebPage page, string command, string? payload = null)
    {
        var result = await SendAsync(page, command, payload ?? $"{{\"pageId\":\"{PageId}\"}}");
        Assert.True(result.Success, result.ErrorCode);
        using var document = JsonDocument.Parse(JsonSerializer.Serialize(result.Payload));
        return document.RootElement.Clone();
    }

    private sealed class RecordingPicker : IPrivacyFilePicker
    {
        public List<string> Calls { get; } = [];

        public string? ExportDestination { get; init; }

        public string? BackupDestination { get; init; }

        public string? BackupSource { get; init; }

        public ValueTask<string?> PickExportDestinationAsync(CancellationToken cancellationToken)
        {
            Calls.Add("export");
            return new ValueTask<string?>(ExportDestination);
        }

        public ValueTask<string?> PickBackupDestinationAsync(CancellationToken cancellationToken)
        {
            Calls.Add("backup");
            return new ValueTask<string?>(BackupDestination);
        }

        public ValueTask<string?> PickBackupSourceAsync(CancellationToken cancellationToken)
        {
            Calls.Add("restore");
            return new ValueTask<string?>(BackupSource);
        }
    }

    private sealed class RecordingExporter : IUserDataExporter
    {
        public List<string> Destinations { get; } = [];

        public Task ExportAsync(string destinationPath, CancellationToken cancellationToken)
        {
            Destinations.Add(destinationPath);
            return Task.CompletedTask;
        }
    }

    private sealed class RecordingDeleter : IUserDataDeleter
    {
        public int DeleteAllCalls { get; private set; }

        public Task DeleteAllAsync(CancellationToken cancellationToken)
        {
            DeleteAllCalls++;
            return Task.CompletedTask;
        }

        public Task DeleteConversationAsync(string conversationId, string servantId, CancellationToken cancellationToken) =>
            Task.CompletedTask;
    }
}
