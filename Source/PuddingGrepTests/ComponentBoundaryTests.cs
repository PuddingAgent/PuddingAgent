using System.Reflection;

namespace PuddingGrepTests;

/// <summary>
/// 规程 S4：边界用**运行时断言**固定，而不是靠文档约定。
/// 匹配内核一旦偷偷引用 Runtime / CodeIndex / Host，就必须当场红。
/// </summary>
[TestClass]
public sealed class ComponentBoundaryTests
{
    private static readonly string[] ForbiddenPrefixes =
    [
        "PuddingRuntime",
        "PuddingCodeIndex",
        "PuddingCodeIntelligence",
        "PuddingHost",
        "PuddingAgent",
        "PuddingPlatform",
        "PuddingFullTextIndex",
    ];

    [TestMethod]
    public void GrepComponent_ReferencesOnlyItselfAndBcl()
    {
        var assembly = typeof(PuddingGrep.GrepMatcher).Assembly;
        var referenced = assembly.GetReferencedAssemblies()
            .Select(name => name.Name ?? string.Empty)
            .Where(name => name.StartsWith("Pudding", StringComparison.Ordinal))
            .ToArray();

        Assert.IsEmpty(referenced,
            "匹配内核不得引用任何 Pudding* 程序集；实际：" + string.Join(", ", referenced));
    }

    [TestMethod]
    public void TestProject_ReferencesOnlyTheComponent()
    {
        var assembly = Assembly.GetExecutingAssembly();
        var referenced = assembly.GetReferencedAssemblies()
            .Select(name => name.Name ?? string.Empty)
            .Where(name => ForbiddenPrefixes.Any(prefix => name.StartsWith(prefix, StringComparison.Ordinal)))
            .Where(name => !name.StartsWith("PuddingGrep", StringComparison.Ordinal))
            .ToArray();

        Assert.IsEmpty(referenced,
            "本测试工程不得引用 Runtime / CodeIndex / Host 等重组件；实际：" + string.Join(", ", referenced));
    }

    /// <summary>
    /// 第二层：csproj 的 ProjectReference。运行时断言看不到「引用了但代码没用」的依赖
    /// （未使用的 ProjectReference 不进程序集引用表），声明层必须单独断言。
    /// </summary>
    [TestMethod]
    public void GrepCsproj_DeclaresNoForbiddenProjectReferences()
    {
        var csproj = GrepCsprojPath();
        Assert.IsTrue(File.Exists(csproj), "找不到 csproj：" + csproj);

        var text = File.ReadAllText(csproj);
        // 只看真正的声明行：注释里会写“零 ProjectReference”，全文匹配会误报。
        var declarations = text
            .Split('\n')
            .Where(line => line.Contains("<ProjectReference", StringComparison.Ordinal))
            .ToArray();

        Assert.IsEmpty(declarations,
            "匹配内核 csproj 不得有任何 ProjectReference（只允许 BCL）；实际：" + string.Join(" | ", declarations));
    }

    /// <summary>用编译期写入的源文件路径推算 csproj（不受输出目录层级影响）。</summary>
    private static string GrepCsprojPath(
        [System.Runtime.CompilerServices.CallerFilePath] string thisFile = "")
    {
        var testProjectDir = Path.GetDirectoryName(thisFile)!;
        var sourceRoot = Path.GetDirectoryName(testProjectDir)!;
        return Path.Combine(sourceRoot, "PuddingGrep", "PuddingGrep.csproj");
    }
}
