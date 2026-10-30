using System.Reflection;

namespace PuddingRetrieval.ContractsTests;

/// <summary>
/// 规程 S4：依赖方向由**运行时断言**固定。
/// 契约组件一旦引用 CodeIndex / Runtime / Host，就必须当场红 —— 那正是 R2-01 要消灭的反向依赖。
/// </summary>
[TestClass]
public sealed class ComponentBoundaryTests
{
    [TestMethod]
    public void Contracts_ReferencesNothingFromPudding()
    {
        var assembly = typeof(PuddingRetrieval.Contracts.CodeSymbolKind).Assembly;
        var referenced = assembly.GetReferencedAssemblies()
            .Select(name => name.Name ?? string.Empty)
            .Where(name => name.StartsWith("Pudding", StringComparison.Ordinal))
            .ToArray();

        Assert.IsEmpty(referenced,
            "Contracts 不得引用任何 Pudding* 程序集；实际：" + string.Join(", ", referenced));
    }

    [TestMethod]
    public void TestProject_DoesNotReferenceImplementations()
    {
        var assembly = Assembly.GetExecutingAssembly();
        var forbidden = new[]
        {
            "PuddingCodeIndex",
            "PuddingCodeIntelligence",
            "PuddingRuntime",
            "PuddingHost",
            "PuddingAgent",
            "PuddingPlatform",
            "PuddingFullTextIndex",
        };

        var referenced = assembly.GetReferencedAssemblies()
            .Select(name => name.Name ?? string.Empty)
            .Where(name => forbidden.Any(prefix => name.StartsWith(prefix, StringComparison.Ordinal)))
            .ToArray();

        Assert.IsEmpty(referenced,
            "契约测试不得引用实现组件；实际：" + string.Join(", ", referenced));
    }
}
