using System.Reflection;
using System.Runtime.CompilerServices;
using PuddingRetrieval;
using PuddingRetrieval.Contracts;

namespace PuddingRetrievalTests;

/// <summary>
/// R2-A 组件边界门禁（S4）。
/// <para>
/// 为什么要有两层：<b>运行时反射只看得见「真正用到的」程序集</b> —— 一个未被使用的
/// <c>ProjectReference</c> 不会进入产物的引用表，所以单靠 <see cref="Assembly.GetReferencedAssemblies"/>
/// 抓不到「在 csproj 里顺手加了一条本来不该加的依赖」。反过来，声明层检查又看不见
/// 「传递引用」。两层缺一不可。
/// </para>
/// </summary>
[TestClass]
public sealed class ComponentBoundaryTests
{
    /// <summary>检索实现层不得依赖的组件（存储 / 抽取 / 运行时 / 宿主 / 平台 / 全文 / 路径 / grep / 用量）。</summary>
    private static readonly string[] Forbidden =
    [
        "PuddingCodeIndex",
        "PuddingCodeIntelligence",
        "PuddingRuntime",
        "PuddingHost",
        "PuddingPlatform",
        "PuddingFullTextIndex",
        "PuddingPathFiltering",
        "PuddingGrep",
        "PuddingLlmUsage",
        "PuddingLlmUsage.Storage",
    ];

    /// <summary>允许依赖的 Pudding 组件（上限，不是必须全部出现）。</summary>
    private static readonly string[] Allowed =
    [
        "PuddingRetrieval.Contracts",
        "PuddingDomain.Contracts",
    ];

    [TestMethod]
    public void Retrieval_ReferencesNoStoreOrHostAssembly()
    {
        var assembly = typeof(IRetrievalSymbolSource).Assembly;

        var puddingRefs = assembly
            .GetReferencedAssemblies()
            .Select(a => a.Name)
            .Where(n => n is not null && n.StartsWith("Pudding", StringComparison.Ordinal))
            .Select(n => n!)
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToArray();

        foreach (var forbidden in Forbidden)
        {
            Assert.IsFalse(
                puddingRefs.Contains(forbidden),
                $"PuddingRetrieval 不得依赖 {forbidden}；实际引用集合 = [{string.Join(", ", puddingRefs)}]");
        }

        var unexpected = puddingRefs.Except(Allowed, StringComparer.Ordinal).ToArray();
        Assert.IsEmpty(
            unexpected,
            $"PuddingRetrieval 只允许依赖 [{string.Join(", ", Allowed)}]，实际多出 [{string.Join(", ", unexpected)}]");
    }

    [TestMethod]
    public void RetrievalCsproj_DeclaresOnlyAllowedProjectReferences()
    {
        var csproj = LocateCsproj();
        var declarations = File
            .ReadAllLines(csproj)
            .Where(line => line.Contains("<ProjectReference", StringComparison.Ordinal))
            .ToArray();

        Assert.HasCount(
            2,
            declarations,
            $"PuddingRetrieval.csproj 应恰好声明 2 条 ProjectReference，实际 {declarations.Length} 条：\n{string.Join("\n", declarations)}");

        foreach (var forbidden in Forbidden)
        {
            Assert.IsFalse(
                declarations.Any(d => d.Contains(forbidden, StringComparison.Ordinal)),
                $"PuddingRetrieval.csproj 不得声明对 {forbidden} 的 ProjectReference。");
        }
    }

    [TestMethod]
    public void RetrievalTextMatch_IsValueEqual()
    {
        var a = new RetrievalTextMatch("a.cs", 7, "x");
        var b = new RetrievalTextMatch("a.cs", 7, "x");

        Assert.AreEqual(a, b, "record 语义必须按值相等 —— 否则去重与比较会把同一行当成两条。");
        Assert.AreNotEqual(a, new RetrievalTextMatch("a.cs", 8, "x"), "行号是身份的一部分。");
    }

    [TestMethod]
    public void RetrievalTextSourceUnavailableException_CarriesInnerException()
    {
        var inner = new InvalidOperationException("boom");
        var ex = new RetrievalTextSourceUnavailableException("index missing", inner);

        Assert.AreEqual("index missing", ex.Message);
        Assert.AreSame(inner, ex.InnerException, "内层异常必须保留 —— 吞掉它等于丢掉真正的失败原因。");
    }

    /// <summary>用 <see cref="CallerFilePathAttribute"/> 定位 csproj：不受 RID 子目录或工作目录影响。</summary>
    private static string LocateCsproj([CallerFilePath] string thisFile = "")
    {
        var componentDir = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(thisFile)!, "..", "PuddingRetrieval"));
        var csproj = Path.Combine(componentDir, "PuddingRetrieval.csproj");

        Assert.IsTrue(File.Exists(csproj), $"定位不到 PuddingRetrieval.csproj：{csproj}");
        return csproj;
    }
}
