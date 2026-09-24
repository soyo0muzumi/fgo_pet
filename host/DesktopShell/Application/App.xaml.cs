using System.Windows;
using System.IO;
using FgoPet.App.Bootstrap;
using FgoPet.App.Lifetime;
using FgoPet.App.Privacy;
using FgoPet.Infrastructure.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Application = System.Windows.Application;

namespace FgoPet.App;

public partial class App : Application
{
    private ServiceProvider? _provider;
    private SingleInstanceCoordinator? _singleInstance;
    private FileStream? _storageOwnership;
    private FgoPet.App.Dialogue.MemoryExtractionQueue? _memoryExtractions;
    private FgoPet.App.Dialogue.DialogueContextLifetime? _dialogueLifetime;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        var activation = e.Args.FirstOrDefault(path => path.EndsWith(".fgopetpack", StringComparison.OrdinalIgnoreCase))
            ?? "--activate";
        var instanceName = Environment.GetEnvironmentVariable("FGO_PET_PIPE_SUFFIX") ?? "main";
        if (!SingleInstanceCoordinator.TryCreatePrimary(instanceName, out _singleInstance, out var isPrimary) || !isPrimary)
        {
            SingleInstanceCoordinator.ForwardActivation(instanceName, activation, TimeSpan.FromSeconds(2));
            Shutdown(0);
            return;
        }

        try
        {
            // The pipe suffix may differ for development instances, but two
            // processes must never write or restore the same storage root.
            var storageRoot = AppPaths.ForCurrentUser().StorageRoot;
            Directory.CreateDirectory(storageRoot);
            _storageOwnership = new FileStream(Path.Combine(storageRoot, ".application-owner.lock"),
                FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            _provider = ServiceRegistration.AddFgoPet(new ServiceCollection(), e.Args).BuildServiceProvider();
            var pendingRestore = _provider.GetRequiredService<PendingBackupRestoreService>();
            var restored = await pendingRestore.ApplyBeforeStartupAsync(
                () => _provider.GetRequiredService<PrivateBackupRestoreService>(), CancellationToken.None);
            if (restored is not null)
            {
                var message = restored.Status switch
                {
                    BackupRestoreStatus.Restored => "私有备份已恢复。缺失的角色包需重新安装；Agent 连接与任务请重新核对。",
                    BackupRestoreStatus.RolledBack => "恢复未完成，已回滚原数据。",
                    BackupRestoreStatus.RecoveryRequired => "恢复未能安全完成，已保留备份和回滚材料。应用已停止启动，请先恢复数据后再打开。",
                    _ => "备份未通过恢复校验，原数据保持不变。",
                };
                MessageBox.Show(message, "私有备份恢复", MessageBoxButton.OK,
                    restored.Status == BackupRestoreStatus.Restored ? MessageBoxImage.Information : MessageBoxImage.Warning);
                if (restored.Status == BackupRestoreStatus.RecoveryRequired) { Shutdown(3); return; }
            }
            _dialogueLifetime = _provider.GetRequiredService<FgoPet.App.Dialogue.DialogueContextLifetime>();
            _memoryExtractions = _provider.GetRequiredService<FgoPet.App.Dialogue.MemoryExtractionQueue>();
            _provider.GetRequiredService<IAppMaintenanceCoordinator>().CompleteStartup();
            _provider.GetRequiredService<Theming.ThemeService>().Initialize();
        }
        catch (Exception error)
        {
            ShowStartupError(error);
            return;
        }

        try
        {
            var startup = _provider.GetRequiredService<AppStartup>();
            var singleInstance = _singleInstance ?? throw new InvalidOperationException("主实例协调器未初始化。");
            singleInstance.ListenForActivation(payload =>
                Dispatcher.BeginInvoke(async () =>
                    await startup.StartAsync(payload == "--activate" ? [] : [payload])));
            await startup.StartAsync(e.Args);
        }
        catch (Exception error)
        {
            ShowStartupError(error);
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        // Fence persisted tickets before disposing UI and network services. Queue continuations never need the Dispatcher.
        _dialogueLifetime?.Invalidate();
        _memoryExtractions?.CancelAndDrainAsync(CancellationToken.None).GetAwaiter().GetResult();
        _provider?.Dispose();
        _storageOwnership?.Dispose();
        _singleInstance?.Dispose();
        base.OnExit(e);
    }

    private void ShowStartupError(Exception error)
    {
        try
        {
            var stateRoot = Environment.GetEnvironmentVariable("FGO_PET_STATE_ROOT");
            if (!string.IsNullOrWhiteSpace(stateRoot))
            {
                Directory.CreateDirectory(stateRoot);
                // Exception.ToString() carries the full stack trace, whose frames embed source
                // absolute paths; redact the exception body only — stateRoot is an operator-configured
                // destination, not user data.
                File.WriteAllText(
                    Path.Combine(stateRoot, "startup-error.log"),
                    $"{DateTimeOffset.UtcNow:O}{Environment.NewLine}{ResourceDiagnostics.Redact($"{error}")}{Environment.NewLine}");
            }
        }
        catch
        {
            // Startup diagnostics must never replace the user-facing error path.
        }

        MessageBox.Show(
            error.Message,
            "FgoPet 启动失败",
            MessageBoxButton.OK,
            MessageBoxImage.Error);
        Shutdown(2);
    }
}
