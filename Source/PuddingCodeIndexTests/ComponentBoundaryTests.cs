using System.Reflection;
using System.Text.Json;

namespace PuddingCodeIndexTests;

/// <summary>
/// S3/S4 boundary assertions for the index component (组件化交付规程 §3 S3/S4).
/// <para>
/// The whole point of <c>PuddingCodeIndexTests</c> is that it can run <b>without</b> the upper layers:
/// no Roslyn, no MSBuild, no <c>PuddingCodeIntelligence</c> / <c>PuddingRuntime</c> / <c>PuddingHost</c> /
/// <c>PuddingAgent</c>. That is what makes "不重启宿主即可开发调试" true for this component.
/// </para>
/// <para>
/// These assertions are deliberately falsifiable — see <c>Boundary_Detector_Flags_Forbidden_Names</c>
/// (instrument self-check) plus the mutations recorded in the Slice 2 report
/// (<c>temp/s2-report.md</c>): temporarily adding a <c>ProjectReference</c> to
/// <c>PuddingCodeIntelligence</c> must turn this suite red.
/// </para>
/// </summary>
[TestClass]
public sealed class ComponentBoundaryTests
{
    /// <summary>Exact upper-layer assembly names that this test process must never depend on.</summary>
    private static readonly string[] ForbiddenAssemblyNames =
    [
        "PuddingCodeIntelligence",
        "PuddingRuntime",
        "PuddingHost",
        "PuddingAgent"
    ];

    /// <summary>Assembly-name prefixes of the deliberately isolated heavy dependencies.</summary>
    private static readonly string[] ForbiddenAssemblyPrefixes =
    [
        "Microsoft.CodeAnalysis",
        "Microsoft.Build"
    ];

    /// <summary>The one and only component this test project is allowed to reference.</summary>
    private const string ComponentAssemblyName = "PuddingCodeIndex";

    /// <summary>
    /// Self-check of the detector itself: a green boundary assertion is worthless if the detector
    /// cannot report a violation. Without this control, "0 violations" could just mean "broken filter".
    /// </summary>
    [TestMethod]
    public void Boundary_Detector_Flags_Forbidden_Names()
    {
        var flagged = FindViolations(
        [
            "PuddingCodeIndex",
            "PuddingCodeIndexTests",
            "MSTest.TestFramework",
            "Microsoft.CodeAnalysis.CSharp",
            "Microsoft.Build.Locator",
            "PuddingCodeIntelligence",
            "PuddingRuntime.Services",
            "PuddingHost",
            "PuddingAgent"
        ]);

        CollectionAssert.AreEquivalent(
            new[]
            {
                "Microsoft.Build.Locator",
                "Microsoft.CodeAnalysis.CSharp",
                "PuddingCodeIntelligence",
                "PuddingHost",
                "PuddingRuntime.Services",
                "PuddingAgent"
            },
            flagged.ToArray(),
            "the boundary detector must flag exactly the forbidden assembly names, and nothing else");

        Assert.IsEmpty(
            FindViolations(["PuddingCodeIndex", "PuddingCodeIndexTests", "MSTest.TestFramework"]),
            "the detector must not flag allowed assemblies");
    }

    /// <summary>
    /// This test process must not have Roslyn / MSBuild / upper-layer assemblies loaded.
    /// The test assembly's metadata references are force-loaded first, so the assertion cannot be
    /// defeated by the CLR's lazy assembly loading (the exact hole that made an AppDomain-only
    /// check unable to go red — see the Slice 2 report).
    /// </summary>
    [TestMethod]
    public void Test_Process_Must_Not_Load_Forbidden_Assemblies()
    {
        var testAssembly = typeof(ComponentBoundaryTests).Assembly;
        Assert.AreEqual(
            "PuddingCodeIndexTests",
            testAssembly.GetName().Name,
            "boundary assertions must run inside the component test assembly");

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
                // Unresolvable references are irrelevant here; the assertion is about *forbidden* names.
            }
        }

        // Assemblies this process can reach through its probing path. Intersected with the declared
        // dependency manifest so that stale build leftovers in bin/ cannot produce a false positive.
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
            "control: the index component itself must be in this process' closure, otherwise the check is vacuous");
        Assert.Contains(
            ComponentAssemblyName,
            reachable,
            "control: the index component must be reachable through the probing path");

        var violations = FindViolations(observed);
        Assert.IsEmpty(
            violations,
            "this test process must not load Roslyn/MSBuild or upper-layer assemblies, but found: "
            + string.Join(", ", violations));
    }

    /// <summary>
    /// The declared runtime dependency closure (<c>*.deps.json</c>) of this test project must not
    /// mention Roslyn / MSBuild / upper-layer assemblies. This is the deterministic tripwire: it is
    /// produced by the SDK from the project's reference closure, so a stray
    /// <c>ProjectReference</c> cannot slip past it (unlike a purely loaded-assembly check).
    /// </summary>
    [TestMethod]
    public void Test_Dependency_Closure_Must_Not_Contain_Forbidden_Assemblies()
    {
        var closure = ReadDependencyClosure();

        Assert.Contains(
            ComponentAssemblyName,
            closure,
            "control: the declared closure must contain the index component, otherwise the check is vacuous");
        Assert.Contains(
            "MSTest.TestFramework",
            closure,
            "control: the declared closure must contain the test framework");

        var violations = FindViolations(closure);
        Assert.IsEmpty(
            violations,
            "the declared dependency closure of PuddingCodeIndexTests must not contain Roslyn/MSBuild "
            + "or upper-layer assemblies, but found: " + string.Join(", ", violations));
    }

    /// <summary>
    /// R2-A（2026-10-31）确立的依赖方向不变量：<c>PuddingCodeIndex</c>（存储/索引实现）
    /// <b>不得</b>引用检索实现 <c>PuddingRetrieval</c>。正确方向是
    /// <c>PuddingRetrieval &lt;- PuddingRetrieval.Adapters -&gt; PuddingCodeIndex</c>。
    /// <para>
    /// 为什么要有这条：把编排从 CodeIndex 搬走后，那条反向 <c>ProjectReference</c> 是
    /// <b>唯一</b>还挡在方向正确性前面的东西。没有断言，任何人补一行引用都不会有测试变红，
    /// 而编译期也不会报错（引用是允许的，只是方向错）。
    /// </para>
    /// <para>
    /// 判据用依赖闭包（<c>*.deps.json</c>）而不是运行时程序集：项目引用即使「只写不引用」
    /// 也会进闭包，而运行时反射看不见未使用的引用。
    /// </para>
    /// </summary>
    [TestMethod]
    public void Dependency_Closure_Must_Not_Contain_RetrievalImplementation()
    {
        var closure = ReadDependencyClosure();

        // 正对照：契约必须在闭包里。它不在，说明闭包读取口径错了 —— 那样下面的负断言就是空转。
        Assert.Contains(
            "PuddingRetrieval.Contracts",
            closure,
            "control: CodeIndex 依赖检索契约 PuddingRetrieval.Contracts，它必须出现在闭包里");

        Assert.DoesNotContain(
            "PuddingRetrieval",
            closure,
            "PuddingCodeIndex 不得引用检索实现 PuddingRetrieval —— 依赖方向必须是 "
            + "PuddingRetrieval <- Adapters -> CodeIndex");
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

    /// <summary>Reads the reached library names out of this test assembly's <c>*.deps.json</c>.</summary>
    private static IReadOnlyList<string> ReadDependencyClosure()
    {
        var assemblyName = typeof(ComponentBoundaryTests).Assembly.GetName().Name!;
        var depsPath = Path.Combine(AppContext.BaseDirectory, assemblyName + ".deps.json");
        Assert.IsTrue(File.Exists(depsPath), $"expected the SDK-generated dependency manifest at {depsPath}");

        using var document = JsonDocument.Parse(File.ReadAllText(depsPath));
        var names = new SortedSet<string>(StringComparer.Ordinal) { assemblyName };

        if (document.RootElement.TryGetProperty("libraries", out var libraries))
            foreach (var library in libraries.EnumerateObject())
                names.Add(StripVersion(library.Name));

        if (document.RootElement.TryGetProperty("targets", out var targets))
            foreach (var target in targets.EnumerateObject())
                foreach (var library in target.Value.EnumerateObject())
                    names.Add(StripVersion(library.Name));

        return names.ToArray();
    }

    private static string StripVersion(string libraryKey)
    {
        var separator = libraryKey.IndexOf('/');
        return separator < 0 ? libraryKey : libraryKey[..separator];
    }
}
