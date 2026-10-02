namespace FgoPet.SettingsHost;

public sealed record SettingsRestoreMetadata(bool AgentPairingRequired);

public interface IApplicationSettingsDocument
{
    string Location { get; }
    string Export();
    SettingsRestoreMetadata ValidateForRestore(string document);
}
