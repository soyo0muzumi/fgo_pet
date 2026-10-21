using FgoPet.Infrastructure.Packs;
using Xunit;

namespace FgoPet.Infrastructure.Tests.Packs;

public sealed class PersonaManifestReaderTests
{
    [Fact]
    public void Resolver_applies_current_appearance_overlay()
    {
        var binding = ContentBindingResolver.Resolve(Fixture("persona-appearance-valid"), "800100", "casual");

        Assert.Equal("800100", binding.Context.ServantId);
        Assert.Equal("test-persona", binding.Context.PackageId);
        Assert.Equal("2.1.0", binding.Context.PersonaVersion);
        Assert.Contains("casual", binding.AppliedLayers);
        Assert.Contains("休闲服", binding.Persona!.FindAppearance("casual")!.Text);
        Assert.Equal(64, binding.PersonaHash.Length);
    }

    [Fact]
    public void Malformed_optional_persona_falls_back_to_null()
    {
        var root = Path.Combine(AppContext.BaseDirectory, "fixtures", "packs", "persona-appearance-invalid");

        Assert.Null(PersonaManifestReader.ReadOptional(root, "800100"));
    }

    [Fact]
    public void Repository_persona_loads_without_hash_fallback()
    {
        var root = Path.Combine(RepositoryRoot(), "content", "packs", "official.mash");

        var persona = PersonaManifestReader.ReadOptional(root, "mash_kyrielight");

        Assert.NotNull(persona);
        Assert.Equal("official.mash", persona.PackageId);
        Assert.False(string.IsNullOrWhiteSpace(persona.CoreText));
    }

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "FgoPet.sln"))) return directory.FullName;
            directory = directory.Parent;
        }
        throw new DirectoryNotFoundException("Cannot locate the repository containing FgoPet.sln.");
    }

    private static string Fixture(string name) =>
        Path.Combine(AppContext.BaseDirectory, "fixtures", "packs", name);
}
