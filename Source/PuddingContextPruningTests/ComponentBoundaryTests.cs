using System.Reflection;
using System.Text.Json;

// 并行化声明：本投影全部用例都是纯内存变换、无共享状态，按方法并行最短。
// 程序集级特性必须住在某个源文件里（位置不影响作用域），放在边界文件便于与「隔离」主题一起阅读。
[assembly: Microsoft.VisualStudio.TestTools.UnitTesting.Parallelize(
    Scope = Microsoft.VisualStudio.TestTools.UnitTesting.ExecutionScope.MethodLevel)]

namespace PuddingContextPruningTests;

/// <summary>
/// S3/S4 边界断言（组件化交付规程 §3 S3/S4）。
/// <para>
/// <c>PuddingContextPruningTests</c> 的全部价值在于它能**脱离宿主**跑：不加载
/// Roslyn / MSBuild / <c>PuddingRuntime</c> / <c>PuddingHost</c> / <c>PuddingAgent</c> /
/// <c>PuddingPlatform</c> / <c>PuddingCodeIntelligence</c> / <c>PuddingCodeIndex</c>。
/// 这正是「不重启宿主即可开发调试」成立的前提。
/// </para>
/// <para>
/// 这些断言是**可证伪**的：<c>Boundary_Detector_Flags_Forbidden_Names</c> 是探针自检，
/// 探针本身失明时它会先红；真正的取红见 <c>temp/s1-mutation-evidence.md</c>
/// （临时加一条指向 <c>PuddingRuntime</c> 的引用必须让本套件变红）。
/// </para>
/// </summary>
[TestClass]
public sealed class ComponentBoundaryTests
{
    /// <summary>本组件永远不得依赖的上层程序集（精确名或前缀 + "."）。</summary>
    private static readonly string[] ForbiddenAssemblyNames =
    [
        "PuddingRuntime",
        "PuddingHost",
        "PuddingAgent",
        "PuddingPlatform",
        "PuddingCodeIntelligence",
        "PuddingCodeIndex"
    ];

    /// <summary>刻意隔离的重型依赖：一旦混进来就不再是「独立可测」的组件。</summary>
    private static readonly string[] ForbiddenAssemblyPrefixes =
    [
        "Microsoft.CodeAnalysis",
        "Microsoft.Build"
    ];

    /// <summary>本测试工程唯一允许引用的被测组件。</summary>
    private const string ComponentAssemblyName = "PuddingContextPruning";

    /// <summary>
    /// 探针自检：绿断言只有再配一个能红的探针才有意义，否则「0 违规」可能只是「过滤器坏了」。
    /// </summary>
    [TestMethod]
    public void Boundary_Detector_Flags_Forbidden_Names()
    {
        var flagged = FindViolations(
        [
            "PuddingContextPruning",
            "PuddingContextPruningTests",
            "MSTest.TestFramework",
            "Microsoft.CodeAnalysis.CSharp",
            "Microsoft.Build.Locator",
            "PuddingCodeIntelligence",
            "PuddingRuntime.Services",
            "PuddingHost",
            "PuddingAgent",
            "PuddingPlatform",
            "PuddingCodeIndex"
        ]);

        CollectionAssert.AreEquivalent(
            new[]
            {
                "Microsoft.Build.Locator",
                "Microsoft.CodeAnalysis.CSharp",
                "PuddingAgent",
                "PuddingCodeIndex",
                "PuddingCodeIntelligence",
                "PuddingHost",
                "PuddingPlatform",
                "PuddingRuntime.Services"
            },
            flagged.ToArray(),
            "探针必须精确标出被禁程序集，且不得误伤允许的程序集");

        Assert.IsEmpty(
            FindViolations(["PuddingContextPruning", "PuddingContextPruningTests", "MSTest.TestFramework"]),
            "探针不得标记允许的程序集");
    }

    /// <summary>
    /// 本测试进程不得加载被禁程序集。先强制加载元数据引用，避免 CLR 惰性加载把违规藏起来。
    /// </summary>
    [TestMethod]
    public void Test_Process_Must_Not_Load_Forbidden_Assemblies()
    {
        var testAssembly = typeof(ComponentBoundaryTests).Assembly;
        Assert.AreEqual(
            "PuddingContextPruningTests",
            testAssembly.GetName().Name,
            "边界断言必须在组件测试程序集内运行");

        var loaded = AppDomain.CurrentDomain.GetAssemblies()
            .Select(a => a.GetName().Name)
            .Where(n => !string.IsNullOrEmpty(n))
            .Select(n => n!)
            .ToArray();

        var referenced = testAssembly.GetReferencedAssemblies()
            .Select(a => a.Name)
            .Where(n => !string.IsNullOrEmpty(n))
            .Select(n => n!)
            .ToArray();

        var forceLoaded = new List<string>();
        foreach (var name in referenced)
        {
            try
            {
                forceLoaded.Add(Assembly.Load(name)!.GetName().Name!);
            }
            catch (Exception)
            {
                // 无法解析的引用与本断言无关：我们只关心被禁名字。
            }
        }

        // 探测路径上可达的程序集，与声明的依赖清单求交，避免 bin 里的陈旧产物造成假阳。
        var declared = ReadDependencyClosure();
        var reachable = Directory.EnumerateFiles(AppContext.BaseDirectory, "*.dll")
            .Select(Path.GetFileNameWithoutExtension)
            .Where(n => !string.IsNullOrEmpty(n))
            .Select(n => n!)
            .Where(n => declared.Contains(n, StringComparer.Ordinal))
            .ToArray();

        var observed = loaded
            .Concat(referenced)
            .Concat(forceLoaded)
            .Concat(reachable)
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        Assert.Contains(
            ComponentAssemblyName,
            observed,
            "对照组：被测组件本身必须在本进程闭包里，否则本检查是空转的");
        Assert.Contains(
            ComponentAssemblyName,
            reachable,
            "对照组：被测组件必须能通过探测路径解析到");

        var violations = FindViolations(observed);
        Assert.IsEmpty(
            violations,
            "本测试进程不得加载 Roslyn/MSBuild 或上层程序集，但发现：" + string.Join(", ", violations));
    }

    /// <summary>
    /// 声明的依赖闭包（<c>*.deps.json</c>）不得出现被禁程序集。这是确定性的绊线：
    /// 由 SDK 依据 ProjectReference 闭包生成，误加一条引用无法绕过。
    /// </summary>
    [TestMethod]
    public void Test_Dependency_Closure_Must_Not_Contain_Forbidden_Assemblies()
    {
        var closure = ReadDependencyClosure();

        Assert.Contains(
            ComponentAssemblyName,
            closure,
            "对照组：声明的闭包必须包含被测组件，否则本检查是空转的");
        Assert.Contains(
            "MSTest.TestFramework",
            closure,
            "对照组：声明的闭包必须包含测试框架");
        Assert.Contains(
            "PuddingCore",
            closure,
            "对照组：声明的闭包必须包含允许的共享模型工程 PuddingCore");

        var violations = FindViolations(closure);
        Assert.IsEmpty(
            violations,
            "PuddingContextPruningTests 的依赖闭包不得包含 Roslyn/MSBuild 或上层程序集，但发现："
            + string.Join(", ", violations));
    }

    private static IReadOnlyList<string> FindViolations(IEnumerable<string> assemblyNames) =>
        assemblyNames
            .Where(n => !string.IsNullOrEmpty(n))
            .Where(IsForbidden)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToArray();

    private static bool IsForbidden(string assemblyName) =>
        ForbiddenAssemblyPrefixes.Any(p => assemblyName.StartsWith(p, StringComparison.Ordinal))
        || ForbiddenAssemblyNames.Any(
            upper => string.Equals(assemblyName, upper, StringComparison.Ordinal)
                     || assemblyName.StartsWith(upper + ".", StringComparison.Ordinal));

    /// <summary>从本测试程序集的 <c>*.deps.json</c> 读出可达库名。</summary>
    private static IReadOnlyList<string> ReadDependencyClosure()
    {
        var assemblyName = typeof(ComponentBoundaryTests).Assembly.GetName().Name!;
        var depsPath = Path.Combine(AppContext.BaseDirectory, assemblyName + ".deps.json");
        Assert.IsTrue(File.Exists(depsPath), $"期望 SDK 生成的依赖清单位于 {depsPath}");

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
