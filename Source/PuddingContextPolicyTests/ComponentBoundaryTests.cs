using System.Reflection;
using System.Text.Json;

namespace PuddingContextPolicyTests;

/// <summary>
/// S4 边界断言（组件化交付规程 §3 S4）。
/// <para>
/// <c>PuddingContextPolicy</c> 是**纯策略**组件：只依赖 BCL。它必须能在宿主运行中独立 build + test，
/// 且**不得**通过任何路径拿到宿主、上层组件或 LLM SDK —— 这是「边界由编译期强制」的运行期复核。
/// </para>
/// <para>
/// 断言是可取红的：<c>Boundary_Detector_Flags_Forbidden_Names</c> 是检测器自检（控制组），
/// 临时给组件加一个指向 <c>PuddingRuntime</c> 的 <c>ProjectReference</c> 会让本套变红。
/// </para>
/// </summary>
[TestClass]
public sealed class ComponentBoundaryTests
{
    private const string ComponentAssemblyName = "PuddingContextPolicy";
    private const string TestAssemblyName = "PuddingContextPolicyTests";

    /// <summary>除组件自身与其测试工程外，任何 <c>Pudding*</c> 程序集都属越界。</summary>
    private static readonly string[] AllowedPuddingAssemblies = [ComponentAssemblyName, TestAssemblyName];

    /// <summary>刻意禁止的重依赖 / LLM SDK 前缀。</summary>
    private static readonly string[] ForbiddenAssemblyPrefixes =
    [
        "Microsoft.CodeAnalysis",
        "Microsoft.Build",
        "OpenAI",
        "Anthropic",
        "Azure.AI",
        "Microsoft.SemanticKernel",
    ];

    /// <summary>检测器自检：绿断言只有在检测器**能报红**时才有意义。</summary>
    [TestMethod]
    public void Boundary_Detector_Flags_Forbidden_Names()
    {
        var flagged = FindViolations(
        [
            ComponentAssemblyName,
            TestAssemblyName,
            "MSTest.TestFramework",
            "System.Text.Json",
            "PuddingRuntime.Services",
            "PuddingCore.Platform",
            "PuddingHost",
            "PuddingAgent",
            "PuddingMemoryEngine",
            "Microsoft.CodeAnalysis.CSharp",
            "OpenAI",
        ]);

        CollectionAssert.AreEquivalent(
            new[]
            {
                "PuddingRuntime.Services",
                "PuddingCore.Platform",
                "PuddingHost",
                "PuddingAgent",
                "PuddingMemoryEngine",
                "Microsoft.CodeAnalysis.CSharp",
                "OpenAI",
            },
            flagged.ToArray(),
            "检测器必须恰好报出越界程序集，且不误报允许项");

        Assert.IsEmpty(
            FindViolations([ComponentAssemblyName, TestAssemblyName, "MSTest.TestFramework", "System.Text.Json"]),
            "检测器不得报出允许的程序集");
    }

    /// <summary>本测试进程不得加载宿主/上层组件/重依赖（先强制加载元数据引用，避免惰性加载漏检）。</summary>
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

        Assert.Contains(ComponentAssemblyName, observed,
            "控制组：策略组件本身必须在该进程闭包内，否则检查是空转的");
        Assert.Contains(ComponentAssemblyName, reachable,
            "控制组：策略组件必须可通过探测路径到达");

        var violations = FindViolations(observed);
        Assert.IsEmpty(
            violations,
            "本测试进程不得加载宿主/上层组件/重依赖，但发现：" + string.Join(", ", violations));
    }

    /// <summary>
    /// 声明的依赖闭包（SDK 生成的 <c>*.deps.json</c>）不得出现越界程序集。
    /// 这是确定性绊线：它由工程的引用闭包生成，游离的 <c>ProjectReference</c> 无法绕过。
    /// </summary>
    [TestMethod]
    public void Test_Dependency_Closure_Must_Not_Contain_Forbidden_Assemblies()
    {
        var closure = ReadDependencyClosure();

        Assert.Contains(ComponentAssemblyName, closure,
            "控制组：声明闭包必须包含策略组件，否则检查是空转的");
        Assert.Contains("MSTest.TestFramework", closure,
            "控制组：声明闭包必须包含测试框架");

        var violations = FindViolations(closure);
        Assert.IsEmpty(
            violations,
            "PuddingContextPolicyTests 的声明依赖闭包不得包含宿主/上层组件/重依赖，但发现："
            + string.Join(", ", violations));
    }

    /// <summary>
    /// 组件程序集的元数据引用里也不得出现越界项（编译期边界的直接复核：
    /// 组件若真的引用了 PuddingRuntime，这里必然报红）。
    /// </summary>
    [TestMethod]
    public void Component_Assembly_References_Must_Stay_BclOnly()
    {
        var component = Assembly.Load(ComponentAssemblyName);
        var references = component.GetReferencedAssemblies()
            .Select(a => a.Name)
            .Where(n => !string.IsNullOrEmpty(n))
            .Select(n => n!)
            .ToArray();

        Assert.IsNotEmpty(references, "控制组：组件程序集应当有元数据引用（至少 System.*）");

        var violations = FindViolations(references);
        Assert.IsEmpty(
            violations,
            "策略组件的元数据引用必须只有 BCL，但发现：" + string.Join(", ", violations));
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
            return true;

        // 任何 Pudding* 程序集：只允许组件自身与它的测试工程。
        if (!assemblyName.StartsWith("Pudding", StringComparison.Ordinal))
            return false;

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
                names.Add(StripVersion(library.Name));
        }

        if (document.RootElement.TryGetProperty("targets", out var targets))
        {
            foreach (var target in targets.EnumerateObject())
            {
                foreach (var library in target.Value.EnumerateObject())
                    names.Add(StripVersion(library.Name));
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
