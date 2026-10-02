using FgoPet.Core.Backup;

namespace FgoPet.App.Privacy;

public enum BackupRestoreStatus
{
    Restored,
    Rejected,
    RolledBack,
    RecoveryRequired,
}

public sealed record BackupRestoreResult(
    BackupRestoreStatus Status,
    BackupFailureCode? FailureCode,
    bool PackageReinstallRequired,
    bool AgentPairingRequired);
