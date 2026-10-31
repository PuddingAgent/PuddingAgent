using System.Reflection;
using System.Runtime.CompilerServices;

namespace PuddingRetrieval.ContractsTests;

/// <summary>
/// 规程 S4：契约组件的边界由**两层**断言固定。
/// <para>
/// 一层是运行时程序集引用（<see cref="Contracts_ReferencesOnlyDomainContractsAndBcl"/>），
/// 二层是 **csproj 的 ProjectReference**（<see cref="ContractsCsproj_DeclaresNoForbiddenProjectReferences"/>）。
/// </para>
/// <para>
/// 为什么必须是两层：实测发现「只在 csproj 里加 ProjectReference、代码里不用」时，
/// 运行时 <c>GetReferencedAssemblies()</c> 里**看不到**那个程序集 —— 一条只做运行时检查的
/// 边界断言对「引用了但没用」完全取不到红，形同摆设。csproj 层才能抓住声明式依赖。
/// </para>
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
        "PuddingGrep",
    ];

    /// <summary>契约程序集在运行时只可依赖 Domain.Contracts（BCL 之外）。</summary>
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

    /// <summary>
    /// 契约 csproj 的 ProjectReference 里不得出现任何被禁工程 —— 这一层抓的是
    /// 「声明了依赖但代码暂时没用」，运行时断言对此无能为力。
    /// </summary>
    [TestMethod]
    public void ContractsCsproj_DeclaresNoForbiddenProjectReferences()
    {
        var csproj = ContractsCsprojPath();
        Assert.IsTrue(File.Exists(csproj), "找不到契约 csproj：" + csproj);

        var text = File.ReadAllText(csproj);
        var forbidden = ForbiddenPrefixes
            .Where(prefix => text.Contains(prefix, StringComparison.Ordinal))
            .ToArray();

        Assert.IsEmpty(forbidden,
            "契约 csproj 不得声明依赖实现组件；实际出现：" + string.Join(", ", forbidden));
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

    /// <summary>
    /// 用编译期写入的源文件路径推算 csproj —— 比从 AppContext.BaseDirectory 上溯稳，
    /// 不受输出目录层级或 RID 子目录影响。
    /// </summary>
    private static string ContractsCsprojPath([CallerFilePath] string thisFile = "")
    {
        var testProjectDir = Path.GetDirectoryName(thisFile)!;         // ...\Source\PuddingRetrieval.ContractsTests
        var sourceRoot = Path.GetDirectoryName(testProjectDir)!;       // ...\Source
        return Path.Combine(sourceRoot, "PuddingRetrieval.Contracts", "PuddingRetrieval.Contracts.csproj");
    }
}
