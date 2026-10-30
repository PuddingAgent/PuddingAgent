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
}
