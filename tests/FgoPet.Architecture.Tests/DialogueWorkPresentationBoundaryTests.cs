using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Xml.Linq;
using Xunit;

namespace FgoPet.Architecture.Tests;

/// <summary>Protect the contract-only Dialogue-to-Work edge after desktop composition moves.</summary>
public sealed class DialogueWorkPresentationBoundaryTests
{
    private const string DialogueProject = "modules/dialogue/src/FgoPet.Dialogue/FgoPet.Dialogue.csproj";
    private static readonly (string Name, string ProjectDirectory)[] PresentationAssemblies =
    [
        ("FgoPet.Dialogue", "modules/dialogue/src/FgoPet.Dialogue"),
        ("FgoPet.DesktopShell", "host/DesktopShell/src/FgoPet.DesktopShell"),
        ("FgoPet.Work.Todo", "modules/work/Todo/src/FgoPet.Work.Todo"),
        ("FgoPet.Work.Archives", "modules/work/Archives/src/FgoPet.Work.Archives"),
    ];

    [Fact]
    public async Task Dialogue_declares_no_Work_or_host_implementation_project_reference()
    {
        var evaluated = await new DotNetMsBuildProjectReferenceEvaluator().EvaluateAsync(
            Path.Combine(FindRepositoryRoot(), DialogueProject), "Release");
        var forbidden = evaluated.References
            .Where(reference => reference.Kind == ProjectReferenceKind.Compile)
            .Select(reference => Path.GetFileNameWithoutExtension(reference.ProjectPath))
            .Where(IsForbiddenImplementation)
            .ToArray();

        Assert.Empty(forbidden);
    }

    [Fact]
    public void Dialogue_does_not_bind_to_Work_or_host_implementations_through_transitive_references()
    {
        var path = AssemblyPath(FindRepositoryRoot(), PresentationAssemblies[0]);
        using var stream = File.OpenRead(path);
        using var pe = new PEReader(stream);
        var metadata = pe.GetMetadataReader();
        var forbidden = metadata.AssemblyReferences
            .Select(handle => metadata.GetString(metadata.GetAssemblyReference(handle).Name))
            .Where(IsForbiddenImplementation)
            .ToArray();

        Assert.Empty(forbidden);
    }

    [Fact]
    public void Dialogue_xaml_does_not_import_Work_or_host_implementation_controls()
    {
        var desktop = Path.Combine(FindRepositoryRoot(), "modules", "dialogue", "Desktop");
        var files = Directory.GetFiles(desktop, "*.xaml", SearchOption.AllDirectories);
        Assert.NotEmpty(files);
        foreach (var path in files)
        {
            var document = XDocument.Load(path);
            Assert.NotNull(document.Root);
            var forbidden = document.Root!.DescendantsAndSelf().Attributes()
                .Where(attribute => attribute.IsNamespaceDeclaration)
                .Select(attribute => attribute.Value.Split(';')
                    .FirstOrDefault(part => part.StartsWith("assembly=", StringComparison.Ordinal)))
                .Where(part => part is not null && IsForbiddenImplementation(part["assembly=".Length..]))
                .ToArray();
            Assert.True(forbidden.Length == 0, $"{path} imports a forbidden implementation control.");
        }
    }

    [Theory]
    [InlineData("FgoPet.App.Dialogue.DialogueWindow", "FgoPet.DesktopShell")]
    [InlineData("FgoPet.App.Views.TodoProposalCard", "FgoPet.Dialogue")]
    [InlineData("FgoPet.App.Views.ArchiveDraftCard", "FgoPet.Dialogue")]
    [InlineData("FgoPet.App.ViewModels.TodoProposalViewModel", "FgoPet.Dialogue")]
    [InlineData("FgoPet.App.ViewModels.ArchiveDraftViewModel", "FgoPet.Dialogue")]
    public void Presentation_types_are_compiled_once_by_their_actual_owner(string typeName, string expectedOwner)
    {
        var root = FindRepositoryRoot();
        var owners = PresentationAssemblies
            .Where(assembly => ReadDefinedTypes(AssemblyPath(root, assembly)).Contains(typeName))
            .Select(assembly => assembly.Name)
            .ToArray();

        Assert.Equal(expectedOwner, Assert.Single(owners));
    }

    private static bool IsForbiddenImplementation(string name) =>
        name.StartsWith("FgoPet.Work.", StringComparison.OrdinalIgnoreCase)
        || string.Equals(name, "FgoPet.DesktopShell", StringComparison.OrdinalIgnoreCase);

    private static string AssemblyPath(string root, (string Name, string ProjectDirectory) assembly)
    {
        var path = Path.Combine(root, assembly.ProjectDirectory, "bin", "Release", "net8.0-windows", assembly.Name + ".dll");
        // Missing output must fail, never silently skip the assembly-boundary check.
        Assert.True(File.Exists(path), $"Missing {path}; build the complete Release solution before running this gate.");
        return path;
    }

    private static IReadOnlySet<string> ReadDefinedTypes(string path)
    {
        using var stream = File.OpenRead(path);
        using var pe = new PEReader(stream);
        var metadata = pe.GetMetadataReader();
        return metadata.TypeDefinitions.Select(handle =>
        {
            var type = metadata.GetTypeDefinition(handle);
            return metadata.GetString(type.Namespace) + "." + metadata.GetString(type.Name);
        }).ToHashSet(StringComparer.Ordinal);
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "FgoPet.sln"))) return directory.FullName;
            directory = directory.Parent;
        }
        throw new DirectoryNotFoundException("Cannot locate the repository containing FgoPet.sln.");
    }
}
