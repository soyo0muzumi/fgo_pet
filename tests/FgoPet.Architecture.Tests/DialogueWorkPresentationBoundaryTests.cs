using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Xml.Linq;
using Xunit;

namespace FgoPet.Architecture.Tests;

/// <summary>Protect the contract-only Dialogue-to-Work edge after desktop composition moves.</summary>
public sealed class DialogueWorkPresentationBoundaryTests
{
    private const string DialogueProject = "plugins/FgoPet.Plugin.Dialogue/FgoPet.Dialogue.csproj";
    private static readonly (string Name, string ProjectDirectory, string TargetFramework)[] PresentationAssemblies =
    [
        ("FgoPet.Dialogue", "plugins/FgoPet.Plugin.Dialogue", "net8.0-windows"),
        ("FgoPet.DesktopShell", "src/FgoPet.Desktop/Shell", "net8.0-windows"),
        ("FgoPet.Plugin.Todo.Desktop", "plugins/FgoPet.Plugin.Todo/Desktop", "net8.0-windows"),
        ("FgoPet.Plugin.Todo.Core", "plugins/FgoPet.Plugin.Todo/Core", "net8.0"),
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
        var desktop = Path.Combine(FindRepositoryRoot(), "plugins", "FgoPet.Plugin.Dialogue", "Desktop");
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
    [InlineData("FgoPet.App.Dialogue.DialogueWindowViewModel", "FgoPet.DesktopShell")]
    public void Presentation_types_are_compiled_once_by_their_actual_owner(string typeName, string expectedOwner)
    {
        var root = FindRepositoryRoot();
        var owners = PresentationAssemblies
            .Where(assembly => ReadDefinedTypes(AssemblyPath(root, assembly)).Contains(typeName))
            .Select(assembly => assembly.Name)
            .ToArray();

        Assert.Equal(expectedOwner, Assert.Single(owners));
    }

    [Theory]
    [InlineData("FgoPet.App.Views.TodoProposalCard")]
    [InlineData("FgoPet.App.Views.ArchiveDraftCard")]
    [InlineData("FgoPet.App.ViewModels.TodoProposalViewModel")]
    [InlineData("FgoPet.App.ViewModels.ArchiveDraftViewModel")]
    public void Retired_action_card_types_are_absent(string typeName)
    {
        Assert.All(PresentationAssemblies, assembly =>
            Assert.DoesNotContain(typeName, ReadDefinedTypes(AssemblyPath(FindRepositoryRoot(), assembly))));
    }

    private static bool IsForbiddenImplementation(string name) =>
        name.StartsWith("FgoPet.Work.", StringComparison.OrdinalIgnoreCase)
        || name.StartsWith("FgoPet.Plugin.Todo.", StringComparison.OrdinalIgnoreCase)
        || string.Equals(name, "FgoPet.DesktopShell", StringComparison.OrdinalIgnoreCase)
        || string.Equals(name, "FgoPet.Character", StringComparison.OrdinalIgnoreCase);

    private static string AssemblyPath(string root, (string Name, string ProjectDirectory, string TargetFramework) assembly)
    {
        var path = Path.Combine(root, assembly.ProjectDirectory, "bin", "Release", assembly.TargetFramework, assembly.Name + ".dll");
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
