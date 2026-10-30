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
}
