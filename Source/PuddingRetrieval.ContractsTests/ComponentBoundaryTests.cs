using System.Reflection;

namespace PuddingRetrieval.ContractsTests;

/// <summary>
/// 规程 S4：契约组件的边界由**运行时断言**固定。
/// 一旦它引用 CodeIndex / CodeIntelligence / Runtime / Host，必须当场红 ——
/// 那正是 R2-01 要消灭的反向依赖。
/// </summary>
[TestClass]
public sealed class ComponentBoundaryTests
{
    private static readonly string[] ForbiddenPrefixes =
    [
        "PuddingCodeIndex",
        "PuddingCodeIntelligence",
        "PuddingRuntime",
        "PuddingHost",
        "PuddingAgent",
        "PuddingPlatform",
        "PuddingFullTextIndex",
        "PuddingPathFiltering",
    ];

    [TestMethod]
    public void Contracts_ReferencesOnlyDomainContractsAndBcl()
    {
        var assembly = typeof(PuddingRetrieval.Contracts.RetrievalRequest).Assembly;
        var referenced = assembly.GetReferencedAssemblies()
            .Select(name => name.Name ?? string.Empty)
            .Where(name => name.StartsWith("Pudding", StringComparison.Ordinal))
            .Where(name => !name.StartsWith("PuddingDomain.Contracts", StringComparison.Ordinal))
            .ToArray();

        Assert.IsEmpty(referenced,
            "检索契约只可依赖 PuddingDomain.Contracts；实际：" + string.Join(", ", referenced));
    }

    [TestMethod]
    public void TestProject_DoesNotReferenceImplementations()
    {
        var assembly = Assembly.GetExecutingAssembly();
        var referenced = assembly.GetReferencedAssemblies()
            .Select(name => name.Name ?? string.Empty)
            .Where(name => ForbiddenPrefixes.Any(prefix => name.StartsWith(prefix, StringComparison.Ordinal)))
            .ToArray();

        Assert.IsEmpty(referenced,
            "契约测试不得引用实现组件；实际：" + string.Join(", ", referenced));
    }
}
