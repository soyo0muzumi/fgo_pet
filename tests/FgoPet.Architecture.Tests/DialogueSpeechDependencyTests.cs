using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using Xunit;

namespace FgoPet.Architecture.Tests;

public sealed class DialogueSpeechDependencyTests
{
    [Fact]
    public async Task Dialogue_project_uses_Speech_Core_without_implementation_references()
    {
        var project = Path.Combine(FindRoot(), "modules/dialogue/src/FgoPet.Dialogue/FgoPet.Dialogue.csproj");
        var evaluation = await new DotNetMsBuildProjectReferenceEvaluator().EvaluateAsync(project, "Release");
        var names = evaluation.References.Where(reference => reference.Kind == ProjectReferenceKind.Compile)
            .Select(reference => Path.GetFileNameWithoutExtension(reference.ProjectPath)).ToArray();
        Assert.Contains("FgoPet.Speech.Core", names);
        Assert.DoesNotContain(names, IsSpeechImplementation);
    }

    [Fact]
    public void Compiled_Dialogue_cannot_use_a_speech_implementation_via_transitive_references()
    {
        var path = Path.Combine(FindRoot(), "modules/dialogue/src/FgoPet.Dialogue/bin/Release/net8.0-windows/FgoPet.Dialogue.dll");
        Assert.True(File.Exists(path), "Build the Release solution before inspecting its assembly references.");
        using var stream = File.OpenRead(path);
        using var pe = new PEReader(stream);
        var metadata = pe.GetMetadataReader();
        var references = metadata.AssemblyReferences.Select(handle =>
            metadata.GetString(metadata.GetAssemblyReference(handle).Name)).ToArray();
        Assert.Contains("FgoPet.Speech.Core", references);
        Assert.DoesNotContain(references, IsSpeechImplementation);
    }

    private static bool IsSpeechImplementation(string name) =>
        name is "FgoPet.Speech.Desktop" or "FgoPet.Speech.Infrastructure";

    private static string FindRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            if (File.Exists(Path.Combine(current.FullName, "FgoPet.sln"))) return current.FullName;
            current = current.Parent;
        }
        throw new DirectoryNotFoundException("Cannot find FgoPet.sln.");
    }
}
