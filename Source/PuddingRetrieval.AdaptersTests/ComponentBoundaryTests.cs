using System.Runtime.CompilerServices;
using System.Text.Json;

namespace PuddingRetrieval.AdaptersTests;

/// <summary>
/// S3/S4 边界断言（组件化交付规程 §3）：<c>PuddingRetrieval.Adapters</c> 是**唯一**允许同时
/// 看见「检索窄端口」与「代码存储」的位置，正因为它能看见两边，它最容易悄悄长歪。
/// <para>
/// 两层把守，缺一不可：
/// </para>
/// <list type="number">
///   <item><description>声明层：<c>PuddingRetrieval.Adapters.csproj</c> 的 <c>ProjectReference</c> 必须
///   <b>恰好</b>是 <c>PuddingRetrieval</c> + <c>PuddingCodeIndex</c> 两条。用「恰好」而不是黑名单：
///   这个组件的职责边界是白名单，多出任何一条都是设计走样。</description></item>
///   <item><description>闭包层：<c>*.deps.json</c> 必须含这两侧、且不含任何上层程序集。声明层看不见
///   「传递依赖」，闭包层能；闭包层看不见「声明了但没用」，声明层能。</description></item>
/// </list>
/// </summary>
[TestClass]
public sealed class ComponentBoundaryTests
{
    /// <summary>适配器层被允许引用的两个组件（不含自身）。</summary>
    private static readonly string[] AllowedReferences =
    [
        "PuddingRetrieval",
        "PuddingCodeIndex"
    ];

    /// <summary>无论以何种方式出现都不允许的上层程序集。</summary>
    private static readonly string[] ForbiddenAssemblyNames =
    [
        "PuddingRuntime",
        "PuddingHost",
        "PuddingPlatform",
        "PuddingAgent",
        "PuddingCodeIntelligence",
        "PuddingFullTextIndex",
        "PuddingCodeIndexTests",
        "PuddingRuntimeTests"
    ];

    /// <summary>
    /// 探测器自检：一条抓不到违规的边界断言等于没写。「0 违规」必须能被证明不是筛子坏了。
    /// </summary>
    [TestMethod]
    public void Boundary_Detector_Flags_Forbidden_Names()
    {
        var flagged = FindViolations(
        [
            "PuddingRetrieval",
            "PuddingCodeIndex",
            "PuddingRetrieval.Adapters",
            "PuddingRuntime",
            "PuddingRuntime.Services",
            "PuddingFullTextIndex"
        ]);

        CollectionAssert.AreEquivalent(
            new[] { "PuddingRuntime", "PuddingRuntime.Services", "PuddingFullTextIndex" },
            flagged.ToArray(),
            "自检：探测器必须恰好标出三个违规项，否则下面的「无违规」结论不可信。");
    }

    /// <summary>
    /// 声明层：csproj 的 <c>ProjectReference</c> 必须恰好是允许的两条。
    /// </summary>
    [TestMethod]
    public void AdaptersCsproj_DeclaresExactlyTheTwoAllowedProjectReferences()
    {
        var csproj = LocateCsproj();
        var declarations = File
            .ReadAllLines(csproj)
            .Where(line => line.Contains("<ProjectReference", StringComparison.Ordinal))
            .ToArray();

        // 正对照：一条都没读到，说明定位或读取口径错了 —— 那样下面的断言就是空转。
        Assert.HasCount(
            2,
            declarations,
            $"PuddingRetrieval.Adapters.csproj 应恰好声明 2 条 ProjectReference，实际 {declarations.Length} 条：\n"
            + string.Join("\n", declarations));

        foreach (var allowed in AllowedReferences)
        {
            Assert.IsTrue(
                declarations.Any(d => d.Contains(allowed + ".csproj", StringComparison.Ordinal)),
                $"PuddingRetrieval.Adapters 必须引用 {allowed} —— 它是唯一能同时看见两侧的位置。");
        }

        var unexpected = declarations
            .Where(d => !AllowedReferences.Any(a => d.Contains(a + ".csproj", StringComparison.Ordinal)))
            .ToArray();

        Assert.IsEmpty(
            unexpected,
            "适配器层只允许引用 PuddingRetrieval 与 PuddingCodeIndex，实际多出：\n"
            + string.Join("\n", unexpected));
    }

    /// <summary>
    /// 闭包层：<c>*.deps.json</c> 必须含两侧、且不含任何上层程序集。
    /// </summary>
    [TestMethod]
    public void Dependency_Closure_ContainsBothSides_AndNoUpperLayers()
    {
        var closure = ReadDependencyClosure();

        Assert.Contains("PuddingRetrieval", closure, "control: 适配器必须有检索窄端口一侧");
        Assert.Contains("PuddingCodeIndex", closure, "control: 适配器必须有代码存储一侧");

        var violations = FindViolations(closure);
        Assert.IsEmpty(
            violations,
            "适配器层的依赖闭包不得包含上层程序集，实际出现："
            + string.Join(", ", violations));
    }

    private static IReadOnlyList<string> FindViolations(IEnumerable<string> assemblyNames) =>
        assemblyNames
            .Where(n => !string.IsNullOrEmpty(n))
            .Where(name => ForbiddenAssemblyNames.Any(
                forbidden => string.Equals(name, forbidden, StringComparison.Ordinal)
                             || name.StartsWith(forbidden + ".", StringComparison.Ordinal)))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToArray();

    /// <summary>用 <see cref="CallerFilePathAttribute"/> 定位 csproj：不受 RID 子目录或工作目录影响。</summary>
    private static string LocateCsproj([CallerFilePath] string thisFile = "")
    {
        var directory = Path.GetDirectoryName(thisFile)
                        ?? throw new InvalidOperationException("无法从 CallerFilePath 推出目录。");

        // 断言的**对象是组件**，不是测试工程：被测 csproj 在隔壁目录。
        // 写成测试工程自身会静默通过（它的 ProjectReference 恰好也可能有 2 条），断言就成了摆设。
        var componentDir = Path.GetFullPath(Path.Combine(directory, "..", "PuddingRetrieval.Adapters"));
        var csproj = Path.Combine(componentDir, "PuddingRetrieval.Adapters.csproj");
        Assert.IsTrue(File.Exists(csproj), $"定位不到 PuddingRetrieval.Adapters.csproj：{csproj}");
        return csproj;
    }

    /// <summary>读本测试程序集 <c>*.deps.json</c> 里被引用的库名（去版本号）。</summary>
    private static IReadOnlyList<string> ReadDependencyClosure()
    {
        var assemblyName = typeof(ComponentBoundaryTests).Assembly.GetName().Name!;
        var depsPath = Path.Combine(AppContext.BaseDirectory, assemblyName + ".deps.json");
        Assert.IsTrue(File.Exists(depsPath), $"expected the SDK-generated dependency manifest at {depsPath}");

        using var document = JsonDocument.Parse(File.ReadAllText(depsPath));
        var names = new SortedSet<string>(StringComparer.Ordinal) { assemblyName };

        if (document.RootElement.TryGetProperty("libraries", out var libraries))
        {
            foreach (var library in libraries.EnumerateObject())
            {
                names.Add(StripVersion(library.Name));
            }
        }

        if (document.RootElement.TryGetProperty("targets", out var targets))
        {
            foreach (var target in targets.EnumerateObject())
            {
                foreach (var library in target.Value.EnumerateObject())
                {
                    names.Add(StripVersion(library.Name));
                }
            }
        }

        return names.ToArray();
    }

    private static string StripVersion(string libraryKey)
    {
        var separator = libraryKey.IndexOf('/');
        return separator < 0 ? libraryKey : libraryKey[..separator];
    }
}
