using System.Reflection;

namespace PuddingDomain.ContractsTests;

/// <summary>
/// 规程 S4：依赖方向由**运行时断言**固定。
/// 共享值类型组件一旦引用 CodeIndex / Retrieval / Runtime / Host，就必须当场红 ——
/// 那说明依赖方向又反了（上层的东西漏进了最底层）。
/// </summary>
[TestClass]
public sealed class ComponentBoundaryTests
{
    private static readonly string[] ForbiddenPrefixes =
    [
        "PuddingCodeIndex",
        "PuddingRetrieval",
        "PuddingCodeIntelligence",
        "PuddingRuntime",
        "PuddingHost",
        "PuddingAgent",
        "PuddingPlatform",
        "PuddingFullTextIndex",
    ];

    [TestMethod]
    public void Contracts_ReferencesNothingFromPudding()
    {
        var assembly = typeof(PuddingCodeIndex.Contracts.CodeSymbolKind).Assembly;
        var referenced = assembly.GetReferencedAssemblies()
            .Select(name => name.Name ?? string.Empty)
            .Where(name => name.StartsWith("Pudding", StringComparison.Ordinal))
            .ToArray();

        Assert.IsEmpty(referenced,
            "共享值类型组件不得引用任何 Pudding* 程序集；实际：" + string.Join(", ", referenced));
    }

    [TestMethod]
    public void TestProject_DoesNotReferenceImplementations()
    {
        var assembly = Assembly.GetExecutingAssembly();
        var referenced = assembly.GetReferencedAssemblies()
            .Select(name => name.Name ?? string.Empty)
            .Where(name => ForbiddenPrefixes.Any(prefix => name.StartsWith(prefix, StringComparison.Ordinal)))
            .Where(name => !name.StartsWith("PuddingDomain", StringComparison.Ordinal))
            .ToArray();

        Assert.IsEmpty(referenced,
            "本测试不得引用实现组件；实际：" + string.Join(", ", referenced));
    }

    /// <summary>
    /// 第二层：csproj 的 ProjectReference。运行时断言看不到「引用了但代码没用」的依赖
    /// （未使用的 ProjectReference 不进程序集引用表），声明层必须单独断言。
    /// </summary>
    [TestMethod]
    public void ContractsCsproj_DeclaresNoForbiddenProjectReferences()
    {
        var csproj = ContractsCsprojPath();
        Assert.IsTrue(File.Exists(csproj), "找不到 csproj：" + csproj);

        var text = File.ReadAllText(csproj);
        // 只看真正的声明行：csproj 注释里会写“不得依赖 PuddingCodeIndex”之类的字样，
        // 全文匹配会把说明文字当成依赖。`<ProjectReference` 才是声明。
        var declarations = text
            .Split('\n')
            .Where(line => line.Contains("<ProjectReference", StringComparison.Ordinal))
            .ToArray();

        // 共享值类型是最底层：它只应依赖 BCL。任何 ProjectReference 都不该有 ——
        // 用“白名单前缀”反而会漏掉新出现的 Pudding 工程。
        Assert.IsEmpty(declarations,
            "共享值类型 csproj 不得有任何 ProjectReference（只允许 BCL）；实际：" + string.Join(" | ", declarations));
    }

    /// <summary>用编译期写入的源文件路径推算 csproj（不受输出目录层级影响）。</summary>
    private static string ContractsCsprojPath(
        [System.Runtime.CompilerServices.CallerFilePath] string thisFile = "")
    {
        var testProjectDir = Path.GetDirectoryName(thisFile)!;
        var sourceRoot = Path.GetDirectoryName(testProjectDir)!;
        return Path.Combine(sourceRoot, "PuddingDomain.Contracts", "PuddingDomain.Contracts.csproj");
    }
}
