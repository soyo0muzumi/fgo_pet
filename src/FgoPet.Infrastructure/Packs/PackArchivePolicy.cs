namespace FgoPet.Infrastructure.Packs;

/// <summary>
/// Fixed extraction limits for a servant pack archive. The production defaults are
/// recorded by tests so both the Python SDK and the .NET installer enforce the same
/// ceilings.
/// </summary>
public sealed record PackArchivePolicy(
    int MaxEntries,
    long MaxEntryBytes,
    long MaxExpandedBytes,
    IReadOnlySet<string> AllowedExtensions)
{
    private const int EntriesPerPack = 1024;
    private const long EntryBytes = 32L * 1024 * 1024;
    private const long ExpandedBytes = 512L * 1024 * 1024;

    public static PackArchivePolicy Production { get; } = new(
        EntriesPerPack,
        EntryBytes,
        ExpandedBytes,
        new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".png", ".jpg", ".jpeg", ".json", ".md", ".txt",
        });
}