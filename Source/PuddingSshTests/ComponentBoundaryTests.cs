using System.Reflection;
using System.Text.Json;

namespace PuddingSshTests;

/// <summary>
/// S4 边界断言（组件化交付规程 §3 S4；设计 §12 A4 会再加变异取红）。
/// <para>
/// <c>PuddingSsh</c> 是叶子组件：允许引用 <c>SSH.NET</c>（实现细节）与 BCL，**不得**引用任何
/// Pudding 上层程序集、EF Core、ASP.NET Core 或 Roslyn/MSBuild。边界由编译期强制，这里做运行期复核。
/// </para>
/// <para>
/// 断言可取红：<c>Boundary_Detector_Flags_Forbidden_Names</c> 是检测器自检（控制组）；
/// 临时给组件或本测试工程加一条指向 <c>PuddingRuntime</c> 的 <c>ProjectReference</c> 会让本套变红。
/// </para>
/// </summary>
[TestClass]
public sealed class ComponentBoundaryTests
{
    private const string ComponentAssemblyName = "PuddingSsh";
    private const string TestAssemblyName = "PuddingSshTests";

    /// <summary>除组件自身与其测试工程外，任何 <c>Pudding*</c> 程序集都属越界。</summary>
    private static readonly string[] AllowedPuddingAssemblies = [ComponentAssemblyName, TestAssemblyName];

    /// <summary>唯一允许的第三方依赖（设计 §3）：它必须**在**闭包里，作为正向控制组。</summary>
    private const string AllowedThirdPartyAssembly = "Renci.SshNet";

    /// <summary>刻意禁止的重依赖 / 上层框架前缀。</summary>
    private static readonly string[] ForbiddenAssemblyPrefixes =
    [
        "Microsoft.CodeAnalysis",
        "Microsoft.Build",
        "Microsoft.EntityFrameworkCore",
        "Microsoft.AspNetCore",
        "OpenAI",
        "Anthropic",
        "Azure.AI",
        "Microsoft.SemanticKernel",
    ];

    [TestMethod]
    public void Boundary_Detector_Flags_Forbidden_Names()
    {
        var flagged = FindViolations(
        [
            ComponentAssemblyName,
            TestAssemblyName,
            "MSTest.TestFramework",
            "Renci.SshNet",
            "System.Text.Json",
            "PuddingRuntime.Services",
            "PuddingCore.Platform",
            "PuddingHost",
            "PuddingAgent",
            "PuddingDesktop",
            "Microsoft.EntityFrameworkCore",
            "Microsoft.AspNetCore.Http",
            "Microsoft.CodeAnalysis.CSharp",
        ]);

        CollectionAssert.AreEquivalent(
            new[]
            {
                "PuddingRuntime.Services",
                "PuddingCore.Platform",
                "PuddingHost",
                "PuddingAgent",
                "PuddingDesktop",
                "Microsoft.EntityFrameworkCore",
                "Microsoft.AspNetCore.Http",
                "Microsoft.CodeAnalysis.CSharp",
            },
            flagged.ToArray(),
            "检测器必须恰好报出越界程序集，且不误报允许项（含唯一允许的 Renci.SshNet）");

        Assert.IsEmpty(
            FindViolations([ComponentAssemblyName, TestAssemblyName, "MSTest.TestFramework", "Renci.SshNet"]),
            "检测器不得报出允许的程序集");
    }

    [TestMethod]
    public void Test_Process_Must_Not_Load_Forbidden_Assemblies()
    {
        var testAssembly = typeof(ComponentBoundaryTests).Assembly;
        Assert.AreEqual(TestAssemblyName, testAssembly.GetName().Name);

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
                // 解析不到的引用与「越界」无关。
            }
        }

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

        Assert.Contains(ComponentAssemblyName, observed, "控制组：组件本身必须在进程闭包内");
        Assert.Contains(AllowedThirdPartyAssembly, observed, "控制组：SSH.NET 必须真的在该进程闭包内");

        var violations = FindViolations(observed);
        Assert.IsEmpty(
            violations,
            "本测试进程不得加载宿主/上层组件/重依赖，但发现：" + string.Join(", ", violations));
    }

    [TestMethod]
    public void Test_Dependency_Closure_Must_ContainComponent_AndNoForbiddenAssemblies()
    {
        var closure = ReadDependencyClosure();

        Assert.Contains(ComponentAssemblyName, closure, "控制组：声明闭包必须包含组件，否则检查是空转的");
        // NuGet 依赖闭包里是**包 ID**（SSH.NET），运行期程序集名是 Renci.SshNet —— 两个都要断言。
        Assert.Contains("SSH.NET", closure, "控制组：声明闭包必须包含 SSH.NET 包");
        Assert.Contains("MSTest.TestFramework", closure, "控制组：声明闭包必须包含测试框架");

        var violations = FindViolations(closure);
        Assert.IsEmpty(
            violations,
            "PuddingSshTests 的声明依赖闭包不得包含宿主/上层组件/重依赖，但发现：" + string.Join(", ", violations));
    }

    [TestMethod]
    public void Component_Assembly_References_Must_StayBclPlusSshNet()
    {
        var component = Assembly.Load(ComponentAssemblyName);
        var references = component.GetReferencedAssemblies()
            .Select(a => a.Name)
            .Where(n => !string.IsNullOrEmpty(n))
            .Select(n => n!)
            .ToArray();

        Assert.IsNotEmpty(references, "控制组：组件程序集应当有元数据引用（至少 System.*）");
        Assert.Contains(AllowedThirdPartyAssembly, references, "控制组：组件应当引用 SSH.NET（传输适配）");

        var violations = FindViolations(references);
        Assert.IsEmpty(
            violations,
            "SSH 组件的元数据引用只能有 BCL + SSH.NET，但发现：" + string.Join(", ", violations));
    }

    [TestMethod]
    public void TestProject_MustOnlyReferenceTheComponent()
    {
        // S2 的直接复核，两路都要查：
        //  (a) 声明闭包（deps.json）—— 能抓到**未被使用**的 ProjectReference（元数据引用里看不到）；
        //  (b) 元数据引用 —— 抓真正编译进去的引用。
        var puddingLibraries = ReadDependencyClosure()
            .Where(n => n.StartsWith("Pudding", StringComparison.Ordinal))
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToArray();

        CollectionAssert.AreEquivalent(
            new[] { ComponentAssemblyName, TestAssemblyName }.OrderBy(n => n, StringComparer.Ordinal).ToArray(),
            puddingLibraries,
            "测试工程的声明闭包里 Pudding* 只能有组件自己与测试工程：多一个就是边界破裂");

        var metadataReferences = typeof(ComponentBoundaryTests).Assembly.GetReferencedAssemblies()
            .Select(a => a.Name)
            .Where(n => !string.IsNullOrEmpty(n) && n!.StartsWith("Pudding", StringComparison.Ordinal))
            .Select(n => n!)
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        CollectionAssert.AreEquivalent(
            new[] { ComponentAssemblyName },
            metadataReferences,
            "测试工程的元数据引用不得包含组件以外的 Pudding* 程序集");
    }

    private static IReadOnlyList<string> FindViolations(IEnumerable<string> assemblyNames) =>
        assemblyNames
            .Where(n => !string.IsNullOrEmpty(n))
            .Where(IsForbidden)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToArray();

    private static bool IsForbidden(string assemblyName)
    {
        if (ForbiddenAssemblyPrefixes.Any(p => assemblyName.StartsWith(p, StringComparison.Ordinal)))
        {
            return true;
        }

        if (!assemblyName.StartsWith("Pudding", StringComparison.Ordinal))
        {
            return false;
        }

        return !AllowedPuddingAssemblies.Any(
            allowed => string.Equals(assemblyName, allowed, StringComparison.Ordinal)
                       || assemblyName.StartsWith(allowed + ".", StringComparison.Ordinal));
    }

    /// <summary>读取本测试程序集 <c>*.deps.json</c> 中声明的库名。</summary>
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
