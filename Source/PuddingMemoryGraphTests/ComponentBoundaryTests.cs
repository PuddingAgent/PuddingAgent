using System.Reflection;
using System.Runtime.CompilerServices;

namespace PuddingMemoryGraphTests;

/// <summary>
/// 规程 S4：边界由**编译期/运行期可验的事实**强制，而不是靠文档约定。
/// <para>
/// 记忆图是「能在没有宿主的进程里单独跑」的组件。它一旦引用了 PuddingRuntime / EF / Roslyn，
/// 就再也不能在轻量测试进程里被验证，也就失去了拆它的意义。
/// </para>
/// </summary>
[TestClass]
public sealed class ComponentBoundaryTests
{
    private static string CsprojPath([CallerFilePath] string thisFile = "") =>
        Path.GetFullPath(Path.Combine(
            Path.GetDirectoryName(thisFile)!,
            "..", "PuddingMemoryGraph", "PuddingMemoryGraph.csproj"));

    [TestMethod]
    public void Component_has_no_project_references()
    {
        var path = CsprojPath();
        Assert.IsTrue(File.Exists(path), $"找不到组件 csproj：{path}");

        var text = File.ReadAllText(path);

        Assert.IsFalse(
            text.Contains("<ProjectReference", StringComparison.OrdinalIgnoreCase),
            "记忆图组件不得引用任何其它工程（依赖方向必须由编译期强制）");
    }

    [TestMethod]
    public void Component_assembly_only_references_the_framework()
    {
        var assembly = typeof(PuddingMemoryGraph.MemoryNode).Assembly;

        var offenders = assembly
            .GetReferencedAssemblies()
            .Select(a => a.Name ?? string.Empty)
            .Where(name => name.StartsWith("Pudding", StringComparison.OrdinalIgnoreCase)
                           || name.StartsWith("Microsoft.EntityFrameworkCore", StringComparison.OrdinalIgnoreCase)
                           || name.StartsWith("Microsoft.CodeAnalysis", StringComparison.OrdinalIgnoreCase))
            .ToList();

        CollectionAssert.AreEqual(
            Array.Empty<string>(),
            offenders,
            "组件程序集不得引用 Pudding* / EF / Roslyn：" + string.Join(", ", offenders));
    }
}
