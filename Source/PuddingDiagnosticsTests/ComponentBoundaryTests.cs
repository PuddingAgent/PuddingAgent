using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;
using PuddingCode.Diagnostics;

namespace PuddingDiagnosticsTests;

/// <summary>
/// S3/S4 边界断言（组件化交付规程 §3）。
/// <para>
/// 承诺：<c>PuddingDiagnostics</c> 是**叶子**——分类规则、证据预算、脱敏与事故投影都能在
/// 不加载 PuddingCore / PuddingRuntime / PuddingPlatform / PuddingHost、不连数据库、
/// 不起日志框架的进程里构建与测试。绿色的「没加载禁用程序集」如果没有能报违规的探测器就是空话，
/// 因此 <see cref="Detector_Flags_Forbidden_Names"/> 是正控制，csproj/源码扫描是机械判据。
/// </para>
/// </summary>
[TestClass]
public sealed class ComponentBoundaryTests
{
    private const string ComponentAssemblyName = "PuddingDiagnostics";

    private static readonly string[] ForbiddenAssemblyNames =
    [
        "PuddingCore",
        "PuddingRuntime",
        "PuddingPlatform",
        "PuddingHost",
        "PuddingAgent",
        "PuddingController",
        "PuddingMemoryEngine",
        "PuddingFullTextIndex",
        "PuddingGateway",
        "PuddingCodeIndex",
        "PuddingCodeIntelligence",
    ];

    private static readonly string[] ForbiddenAssemblyPrefixes =
    [
        "Serilog",
        "Microsoft.EntityFrameworkCore",
        "Microsoft.AspNetCore",
        "Microsoft.Extensions.DependencyInjection",
        "Microsoft.Data.Sqlite",
    ];

    /// <summary>
    /// 只对**组件自身**成立的禁用前缀：测试宿主必须加载测试框架，把 MSTest 放进进程级判据
    /// 会让探测器乱报警，最后被人关掉（`组件化交付规程` §3.2 的反例）。
    /// </summary>
    private static readonly string[] ForbiddenComponentOnlyPrefixes = ["MSTest"];

    /// <summary>组件源码里不允许出现的实现手段（出现即越界，不是风格问题）。</summary>
    private static readonly string[] ForbiddenSourceTokens =
    [
        "Serilog",
        "DbContext",
        "EntityFrameworkCore",
        "Microsoft.AspNetCore",
        "new HttpClient",
        "File.WriteAllText",
    ];

    [TestMethod]
    public void Detector_Flags_Forbidden_Names()
    {
        var flagged = FindViolations(
        [
            "PuddingDiagnostics",
            "PuddingDiagnosticsTests",
            "System.Runtime",
            "System.Net.Http",
            "MSTest.TestFramework",
            "Serilog",
            "Microsoft.EntityFrameworkCore",
            "Microsoft.AspNetCore.Http",
            "PuddingRuntime.Services",
            "PuddingPlatform.Data",
            "PuddingCore.Observability",
            "PuddingHost",
            "PuddingAgent",
        ]);

        CollectionAssert.AreEquivalent(
            new[]
            {
                "Microsoft.AspNetCore.Http",
                "Microsoft.EntityFrameworkCore",
                "PuddingAgent",
                "PuddingCore.Observability",
                "PuddingHost",
                "PuddingPlatform.Data",
                "PuddingRuntime.Services",
                "Serilog",
            },
            flagged.ToArray(),
            "边界探测器必须精确命中禁用项，多报少报都算失效；测试框架由宿主加载，不得计入");

        Assert.IsTrue(
            ForbiddenComponentOnlyPrefixes.Any(prefix =>
                "MSTest.TestFramework".StartsWith(prefix, StringComparison.Ordinal)),
            "正控制：组件级禁用前缀必须能命中测试框架名");

        Assert.IsEmpty(
            FindViolations([ComponentAssemblyName, "PuddingDiagnosticsTests", "System.Net.Sockets"]),
            "合法程序集不得被标记");
    }

    [TestMethod]
    public void Source_Token_Detector_Flags_Forbidden_Means()
    {
        const string offending = """
            var logger = new Serilog.LoggerConfiguration();
            using var db = new PlatformDbContext();
            var client = new HttpClient();
            """;

        var flagged = FindForbiddenSourceTokens(offending);

        CollectionAssert.AreEquivalent(new[] { "DbContext", "Serilog", "new HttpClient" }, flagged.ToArray());
        Assert.IsEmpty(FindForbiddenSourceTokens("public static class DiagnosticEvidenceRedactor { }"));
    }

    [TestMethod]
    public void Component_Sources_UseOnlyBclAndNeverSpeakToAStoreOrLogger()
    {
        var projectDirectory = FindRepositoryFile(Path.Combine("Source", ComponentAssemblyName));
        Assert.IsTrue(Directory.Exists(projectDirectory), $"期望组件目录位于 {projectDirectory}");

        var sources = Directory.EnumerateFiles(projectDirectory, "*.cs", SearchOption.AllDirectories).ToArray();
        Assert.IsNotEmpty(sources, "正控制：扫描必须真的有文件可读");

        var offenders = new List<string>();
        foreach (var source in sources)
        {
            var flagged = FindForbiddenSourceTokens(File.ReadAllText(source));
            if (flagged.Count > 0)
                offenders.Add($"{Path.GetFileName(source)}: {string.Join(", ", flagged)}");
        }

        Assert.IsEmpty(offenders, "组件不得写库/写日志/直连 HTTP：" + string.Join(" | ", offenders));
    }

    [TestMethod]
    public void Component_Csproj_DeclaresZeroProjectAndPackageReferences()
    {
        var csprojPath = FindRepositoryFile(Path.Combine("Source", ComponentAssemblyName, ComponentAssemblyName + ".csproj"));
        Assert.IsTrue(File.Exists(csprojPath), $"期望组件 csproj 位于 {csprojPath}");

        var text = File.ReadAllText(csprojPath);

        Assert.IsFalse(text.Contains("<ProjectReference", StringComparison.OrdinalIgnoreCase),
            "叶子组件不得声明 ProjectReference");
        Assert.IsFalse(text.Contains("<PackageReference", StringComparison.OrdinalIgnoreCase),
            "叶子组件不得声明 PackageReference");
        Assert.IsFalse(text.Contains("InternalsVisibleTo", StringComparison.OrdinalIgnoreCase),
            "叶子组件不得对上层开放 internals；只走公开 API");
    }

    [TestMethod]
    public void Test_Csproj_ReferencesOnlyTheComponent()
    {
        var csprojPath = FindRepositoryFile(
            Path.Combine("Source", "PuddingDiagnosticsTests", "PuddingDiagnosticsTests.csproj"));
        Assert.IsTrue(File.Exists(csprojPath), $"期望测试 csproj 位于 {csprojPath}");

        var text = File.ReadAllText(csprojPath);
        var references = Regex
            .Matches(text, @"<ProjectReference\s+Include=""([^""]+)""")
            .Select(match => match.Groups[1].Value.Replace('\\', '/'))
            .ToArray();

        CollectionAssert.AreEqual(
            new[] { "../PuddingDiagnostics/PuddingDiagnostics.csproj" },
            references,
            "组件测试工程必须只引用组件本身（S2）");
    }

    [TestMethod]
    public void Test_Process_MustNotLoadForbiddenAssemblies()
    {
        var testAssembly = typeof(ComponentBoundaryTests).Assembly;
        Assert.AreEqual("PuddingDiagnosticsTests", testAssembly.GetName().Name,
            "边界断言必须跑在组件测试程序集内");

        var loaded = AppDomain.CurrentDomain.GetAssemblies()
            .Select(assembly => assembly.GetName().Name)
            .Where(name => !string.IsNullOrEmpty(name))
            .Select(name => name!)
            .ToArray();

        var referenced = testAssembly.GetReferencedAssemblies()
            .Select(assembly => assembly.Name)
            .Where(name => !string.IsNullOrEmpty(name))
            .Select(name => name!)
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
                // 无法解析的引用不构成越界依赖。
            }
        }

        var declared = ReadDependencyClosure();
        var reachable = Directory.EnumerateFiles(AppContext.BaseDirectory, "*.dll")
            .Select(Path.GetFileNameWithoutExtension)
            .Where(name => !string.IsNullOrEmpty(name))
            .Select(name => name!)
            .Where(name => declared.Contains(name, StringComparer.Ordinal))
            .ToArray();

        var observed = loaded
            .Concat(referenced)
            .Concat(forceLoaded)
            .Concat(reachable)
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        Assert.Contains(ComponentAssemblyName, observed, "正控制：组件本身必须在进程依赖闭包里");
        Assert.Contains(ComponentAssemblyName, reachable, "正控制：组件必须经探测路径可达");

        var violations = FindViolations(observed);
        Assert.IsEmpty(violations, "组件必须保持叶子；加载/声明了上层：" + string.Join(", ", violations));
    }

    [TestMethod]
    public void Component_Assembly_MustNotReferenceUpperLayers()
    {
        var referenced = typeof(DiagnosticCause).Assembly
            .GetReferencedAssemblies()
            .Select(assembly => assembly.Name)
            .Where(name => !string.IsNullOrEmpty(name))
            .Select(name => name!)
            .ToArray();

        Assert.Contains("System.Runtime", referenced, "正控制：组件程序集必须能读出引用表");
        Assert.IsEmpty(FindViolations(referenced),
            "组件不得引用上层/日志/EF/ASP.NET：" + string.Join(", ", referenced));
        Assert.IsFalse(
            referenced.Any(name => ForbiddenComponentOnlyPrefixes.Any(prefix => name.StartsWith(prefix, StringComparison.Ordinal))),
            "组件不得引用测试框架：" + string.Join(", ", referenced));
    }

    private static bool IsForbidden(string assemblyName) =>
        ForbiddenAssemblyPrefixes.Any(prefix => assemblyName.StartsWith(prefix, StringComparison.Ordinal))
        || ForbiddenAssemblyNames.Any(upper =>
            string.Equals(assemblyName, upper, StringComparison.Ordinal)
            || assemblyName.StartsWith(upper + ".", StringComparison.Ordinal));

    private static List<string> FindViolations(IEnumerable<string> assemblyNames) =>
        assemblyNames.Where(IsForbidden).OrderBy(name => name, StringComparer.Ordinal).ToList();

    private static List<string> FindForbiddenSourceTokens(string text) =>
        ForbiddenSourceTokens
            .Where(token => text.Contains(token, StringComparison.Ordinal))
            .OrderBy(token => token, StringComparer.Ordinal)
            .ToList();

    private static string FindRepositoryFile(string relativePath)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "PuddingAgentNetwork.slnx")))
                return Path.Combine(directory.FullName, relativePath);

            directory = directory.Parent;
        }

        Assert.Fail("未能从 " + AppContext.BaseDirectory + " 向上找到仓库根（PuddingAgentNetwork.slnx）");
        return string.Empty;
    }

    private static IReadOnlyList<string> ReadDependencyClosure()
    {
        var assemblyName = typeof(ComponentBoundaryTests).Assembly.GetName().Name!;
        var depsPath = Path.Combine(AppContext.BaseDirectory, assemblyName + ".deps.json");
        Assert.IsTrue(File.Exists(depsPath), $"期望 SDK 生成的依赖清单位于 {depsPath}");

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
