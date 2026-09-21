namespace FgoPet.Infrastructure.Secrets;

/// <summary>Compatibility namespace for the Windows Credential Manager adapter.</summary>
public interface ICredentialStore : FgoPet.Core.Secrets.ICredentialStore
{
}

/// <summary>Compatibility namespace for provider-facing protected reads.</summary>
public interface ICredentialReader : FgoPet.Core.Secrets.ICredentialReader
{
}