using System.Reflection;
using System.Text.Json;

namespace PuddingBrowser.AutomationTests;

/// <summary>
/// S3/S4 边界断言（组件化交付规程 §3）。
///
/// 为什么这些断言必须存在：<c>PuddingBrowser.Automation</c> 的全部价值在于它是一个
/// <b>可以不加载宿主/驱动/UI 就被构建与测试</b>的纯逻辑组件。一旦有人为了图方便加一条
/// <c>ProjectReference</c>，边界就没了，而「编译通过 + 我跑的那几个测试绿」都看不出来。
///
/// 断言故意可证伪（见 <c>Boundary_Detector_Flags_Forbidden_Names</c> 的自检控制）：
/// 临时加一条指向 <c>Pudding.DesktopService</c> 的引用，本套测试必须变红。
/// </summary>
[TestClass]
public sealed class ComponentBoundaryTests
{
    /// <summary>本组件（唯一允许的自身程序集）。</summary>
    private const string ComponentAssemblyName = "PuddingBrowser.Automation";

    /// <summary>唯一允许被引用的更低层组件（BCL-only 叶子）。</summary>
    private const string AllowedLeafAssemblyName = "Pudding.Contracts";

    /// <summary>本组件的测试程序集（自身不是依赖，必须放行）。</summary>
    private const string TestAssemblyName = "PuddingBrowser.AutomationTests";

    /// <summary>允许出现的 Pudding* 程序集：组件自身、其允许的叶子、以及组件测试程序集。</summary>
    private static readonly string[] AllowedPuddingAssemblies =
    [
        ComponentAssemblyName,
        AllowedLeafAssemblyName,
        TestAssemblyName,
    ];

    /// <summary>刻意隔离的重依赖 / UI / 驱动前缀。</summary>
    private static readonly string[] ForbiddenAssemblyPrefixes =
    [
        "Microsoft.CodeAnalysis",
        "Microsoft.Build",
        "Microsoft.Web.WebView2",
        "Microsoft.WindowsAppSDK",
        "Microsoft.WinUI",
        "Microsoft.UI.Xaml",
        "PresentationFramework",
        "PresentationCore",
        "System.Windows.Forms",
    ];

    /// <summary>
    /// 规则：任何 <c>Pudding*</c> 程序集，只要不是本组件或允许的叶子，都是越界。
    /// 这比枚举黑名单更强 —— 新增一个消费方组件也会被抓到。
    /// </summary>
    private static bool IsForbidden(string assemblyName)
    {
        if (assemblyName.StartsWith("Pudding", StringComparison.Ordinal))
        {
            return !AllowedPuddingAssemblies.Contains(assemblyName, StringComparer.Ordinal);
        }

        return ForbiddenAssemblyPrefixes.Any(prefix => assemblyName.StartsWith(prefix, StringComparison.Ordinal));
    }

    private static IReadOnlyList<string> FindViolations(IEnumerable<string> assemblyNames) =>
        assemblyNames
            .Where(name => !string.IsNullOrEmpty(name))
            .Where(IsForbidden)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();

    [TestMethod]
    public void Boundary_Detector_Flags_Forbidden_Names()
    {
        var flagged = FindViolations(
        [
            ComponentAssemblyName,
            "PuddingBrowser.AutomationTests",
            "Pudding.Contracts",
            "MSTest.TestFramework",
            "Pudding.DesktopService",
            "Pudding.DesktopSurface.Browser",
            "PuddingHost",
            "PuddingRuntime",
            "PuddingAgent",
            "PuddingDesktop",
            "PuddingBrowser.WebView2",
            "PuddingBrowser.WinUI",
            "PuddingBrowser.AgentTools",
            "PuddingBrowser.Abstractions",
            "Microsoft.Web.WebView2.Core",
            "Microsoft.WindowsAppSDK",
            "Microsoft.UI.Xaml",
            "Microsoft.CodeAnalysis.CSharp",
        ]);

        CollectionAssert.AreEquivalent(
            new[]
            {
                "Microsoft.CodeAnalysis.CSharp",
                "Microsoft.UI.Xaml",
                "Microsoft.Web.WebView2.Core",
                "Microsoft.WindowsAppSDK",
                "Pudding.DesktopService",
                "Pudding.DesktopSurface.Browser",
                "PuddingAgent",
                "PuddingBrowser.Abstractions",
                "PuddingBrowser.AgentTools",
                "PuddingBrowser.WebView2",
                "PuddingBrowser.WinUI",
                "PuddingDesktop",
                "PuddingHost",
                "PuddingRuntime",
            },
            flagged.ToArray(),
            "the boundary detector must flag exactly the forbidden assembly names, and nothing else");

        Assert.IsEmpty(
            FindViolations([ComponentAssemblyName, "Pudding.Contracts", "MSTest.TestFramework"]),
            "the detector must not flag the component or its allowed leaf");
    }

    [TestMethod]
    public void Test_Process_Must_Not_Load_Forbidden_Assemblies()
    {
        var testAssembly = typeof(ComponentBoundaryTests).Assembly;
        Assert.AreEqual("PuddingBrowser.AutomationTests", testAssembly.GetName().Name);

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

        // 强制加载声明的引用，避免 CLR 惰性加载让「未加载」变成假绿。
        var forceLoaded = new List<string>();
        foreach (var name in referenced)
        {
            try
            {
                forceLoaded.Add(Assembly.Load(name)!.GetName().Name!);
            }
            catch (Exception)
            {
                // 解析不了的引用与本断言无关。
            }
        }

        // 探测路径上可达的程序集，与声明的依赖清单取交集（避免 bin 里的陈旧残留造成假红）。
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

        // 控制组：组件与允许的叶子必须在闭包里，否则本检查是空转。
        Assert.Contains(ComponentAssemblyName, observed, "control: the component must be in this process");
        Assert.Contains(AllowedLeafAssemblyName, observed, "control: the allowed leaf must be in this process");

        var violations = FindViolations(observed);
        Assert.IsEmpty(
            violations,
            "this test process must not load the host, the driver, the UI or any other consumer, but found: "
            + string.Join(", ", violations));
    }

    [TestMethod]
    public void Test_Dependency_Closure_Must_Not_Contain_Forbidden_Assemblies()
    {
        var closure = ReadDependencyClosure();

        Assert.Contains(ComponentAssemblyName, closure, "control: the declared closure must contain the component");
        Assert.Contains(AllowedLeafAssemblyName, closure, "control: the declared closure must contain Pudding.Contracts");
        Assert.Contains("MSTest.TestFramework", closure, "control: the declared closure must contain the test framework");

        var violations = FindViolations(closure);
        Assert.IsEmpty(
            violations,
            "the declared dependency closure of PuddingBrowser.AutomationTests must stay at "
            + "PuddingBrowser.Automation + Pudding.Contracts, but found: " + string.Join(", ", violations));
    }

    [TestMethod]
    public void Component_Project_File_Keeps_The_Declared_Reference_List()
    {
        var projectPath = Path.Combine(
            RepoRoot(),
            "Source",
            "PuddingBrowser.Automation",
            "PuddingBrowser.Automation.csproj");

        Assert.IsTrue(File.Exists(projectPath), $"expected the component project at {projectPath}");

        var text = File.ReadAllText(projectPath);

        var references = System.Text.RegularExpressions.Regex
            .Matches(text, @"<ProjectReference\s+Include=""([^""]+)""")
            .Select(match => match.Groups[1].Value.Replace('\\', '/'))
            .ToArray();

        CollectionAssert.AreEquivalent(
            new[] { "../Pudding.Contracts/Pudding.Contracts.csproj" },
            references,
            "the component may reference Pudding.Contracts ONLY");

        Assert.IsFalse(
            text.Contains("PackageReference Include=", StringComparison.Ordinal),
            "the component must stay package-free (pure logic, no Roslyn/MSBuild/WebView2 packages)");

        // 编译期门禁必须真的存在（否则边界只剩运行时断言）。
        StringAssert.Contains(text, "EnforceAutomationBoundary");
    }

    [TestMethod]
    public void Test_Project_File_References_Only_The_Component()
    {
        var projectPath = Path.Combine(
            RepoRoot(),
            "Source",
            "PuddingBrowser.AutomationTests",
            "PuddingBrowser.AutomationTests.csproj");

        Assert.IsTrue(File.Exists(projectPath), $"expected the component test project at {projectPath}");

        var text = File.ReadAllText(projectPath);
        var references = System.Text.RegularExpressions.Regex
            .Matches(text, @"<ProjectReference\s+Include=""([^""]+)""")
            .Select(match => match.Groups[1].Value.Replace('\\', '/'))
            .ToArray();

        CollectionAssert.AreEquivalent(
            new[] { "../PuddingBrowser.Automation/PuddingBrowser.Automation.csproj" },
            references,
            "the component test project may reference PuddingBrowser.Automation ONLY (S2)");
    }

    /// <summary>读取本测试程序集的 <c>*.deps.json</c> 里的库清单。</summary>
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

    /// <summary>从测试输出目录向上找解决方案根（不依赖硬编码的绝对路径）。</summary>
    private static string RepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "PuddingAgentNetwork.slnx")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new InvalidOperationException(
            $"could not locate the repository root above {AppContext.BaseDirectory}");
    }
}
