using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Text.RegularExpressions;
using Xunit;

namespace FgoPet.Architecture.Tests;

/// <summary>
/// 传递 implementation 依赖门禁（任务卡 A 步骤 3）。
///
/// SDK-style 项目会把 <c>ProjectReference</c> 目标及其**传递依赖**一并暴露给编译期，
/// 所以 "App 没声明却直接 new 了 Speech.Infrastructure 的类型" 不会报编译错误，
/// 只会悄悄变成一条谁都看不见的耦合。静态 A→B 一层检查抓不到这类边，
/// 必须看**编译产物实际绑定了哪些程序集**。
/// </summary>
public sealed class TransitiveImplementationReferenceTests
{
    private const string AppProjectRelativePath = "src/FgoPet.App/FgoPet.App.csproj";
    private const string AppAssemblyRelativePath = "src/FgoPet.App/bin/Release/net8.0-windows/FgoPet.App.dll";

    [Fact]
    public void App_binds_only_to_the_implementation_assemblies_it_declares()
    {
        var root = FindRepositoryRoot();
        var assemblyPath = Path.GetFullPath(Path.Combine(root, AppAssemblyRelativePath));

        // 找不到产物就红，不跳过——跳过会产生假绿，这正是本项目已踩过的坑。
        Assert.True(File.Exists(assemblyPath), $"未找到 {assemblyPath}；本测试需要 FgoPet.App 的 Release 构建产物。");

        var declared = ReadDeclaredProjectReferences(Path.Combine(root, AppProjectRelativePath));
        var bound = ReadBoundAssemblyNames(assemblyPath);

        var undeclaredImplementations = bound
            .Where(name => name.EndsWith(".Infrastructure", StringComparison.OrdinalIgnoreCase))
            .Where(name => !declared.Contains(name))
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();

        Assert.Empty(undeclaredImplementations);
    }

    /// <summary>App.csproj 里显式声明的项目引用（按程序集名）。</summary>
    private static IReadOnlySet<string> ReadDeclaredProjectReferences(string projectPath)
    {
        Assert.True(File.Exists(projectPath), $"未找到 {projectPath}。");

        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match match in Regex.Matches(
                     File.ReadAllText(projectPath),
                     "<ProjectReference\\s+Include=\"(?<path>[^\"]+)\"",
                     RegexOptions.IgnoreCase))
        {
            names.Add(Path.GetFileNameWithoutExtension(match.Groups["path"].Value));
        }

        Assert.NotEmpty(names);
        return names;
    }

    /// <summary>读取 PE 的 AssemblyRef 表——即编译期真实绑定到的程序集，含传递依赖。</summary>
    private static IReadOnlySet<string> ReadBoundAssemblyNames(string assemblyPath)
    {
        using var stream = File.OpenRead(assemblyPath);
        using var peReader = new PEReader(stream);
        var metadata = peReader.GetMetadataReader();

        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var handle in metadata.AssemblyReferences)
        {
            names.Add(metadata.GetString(metadata.GetAssemblyReference(handle).Name));
        }

        return names;
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "FgoPet.sln")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException($"无法从 {AppContext.BaseDirectory} 向上定位到含 FgoPet.sln 的仓库根目录。");
    }
}
