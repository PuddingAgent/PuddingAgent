using System.Reflection;
using System.Runtime.CompilerServices;

namespace PuddingCodeIntelligenceTests;

/// <summary>
/// S3/S4 边界断言（组件化交付规程 §3）：<c>PuddingCodeIntelligence</c> 是 Roslyn 抽取层，
/// 只应依赖 <c>PuddingCodeIndex</c>（<c>ICodeIndexer</c> 的定义方）。它一旦反向依赖
/// Runtime / Host / Platform，就会把整个宿主拖进「本可独立测试」的抽取层。
/// <para>
/// 2026-10-31 发现并修正一处：本测试工程此前**没有**直接引用被测组件，靠
/// <c>PuddingRuntime</c> 传递才编得过 —— 「测 X 的工程不引用 X」。删掉那个 Runtime 引用后
/// 立刻暴露（CS0246）。现两条都锁死：组件只能依赖 CodeIndex，测试工程必须显式引用组件且不得引用 Runtime。
/// </para>
/// </summary>
[TestClass]
public sealed class ComponentBoundaryTests
{
    private const string ComponentName = "PuddingCodeIntelligence";

    /// <summary>组件唯一允许的项目引用（ICodeIndexer 的定义方）。</summary>
    private const string AllowedComponentReference = "PuddingCodeIndex";

    /// <summary>组件程序集不得直接引用的上层程序集。</summary>
    private static readonly string[] ForbiddenAssemblyNames =
    [
        "PuddingRuntime",
        "PuddingHost",
        "PuddingPlatform",
        "PuddingAgent",
        "PuddingFullTextIndex",
        "PuddingBrowser.AgentTools"
    ];

    /// <summary>探测器自检：抓不到违规的边界断言等于没写。</summary>
    [TestMethod]
    public void Boundary_Detector_Flags_Forbidden_Names()
    {
        var flagged = FindViolations(
        [
            "PuddingCodeIndex",
            "PuddingCodeIntelligence",
            "PuddingRuntime",
            "PuddingRuntime.Services",
            "PuddingFullTextIndex"
        ]);

        CollectionAssert.AreEquivalent(
            new[] { "PuddingRuntime", "PuddingRuntime.Services", "PuddingFullTextIndex" },
            flagged.ToArray(),
            "自检：探测器必须恰好标出三个违规项，否则下面的「无违规」结论不可信。");
    }

    /// <summary>声明层：组件 csproj 必须恰好一条 <c>ProjectReference</c>，且指向 <c>PuddingCodeIndex</c>。</summary>
    [TestMethod]
    public void ComponentCsproj_DeclaresOnlyCodeIndex()
    {
        var csproj = LocateCsproj(ComponentName);
        var declarations = ReadProjectReferences(csproj);

        Assert.HasCount(
            1,
            declarations,
            $"{ComponentName}.csproj 应恰好声明 1 条 ProjectReference，实际 {declarations.Length} 条：\n"
            + string.Join("\n", declarations));

        Assert.IsTrue(
            declarations[0].Contains(AllowedComponentReference + ".csproj", StringComparison.Ordinal),
            $"Roslyn 抽取层只应依赖 {AllowedComponentReference}（ICodeIndexer 的定义方），实际：{declarations[0]}");
    }

    /// <summary>
    /// 声明层（测试侧）：测试工程必须**显式**引用被测组件，且不得再借 <c>PuddingRuntime</c> 传递。
    /// </summary>
    [TestMethod]
    public void TestCsproj_ReferencesComponentExplicitly_AndNotRuntime()
    {
        var csproj = LocateCsproj("PuddingCodeIntelligenceTests");
        var declarations = ReadProjectReferences(csproj);

        // 正对照：一条都没读到，说明定位/读取口径错了，下面两条断言就是空转。
        Assert.IsNotEmpty(declarations, $"未能从 {csproj} 读到任何 ProjectReference，判据不可信。");

        Assert.IsTrue(
            declarations.Any(d => d.Contains(ComponentName + ".csproj", StringComparison.Ordinal)),
            "测试工程必须显式引用被测组件 —— 靠别的工程传递引用时，删掉那条传递边会以 CS0246 的形式突然爆炸。");

        Assert.IsFalse(
            declarations.Any(d => d.Contains("PuddingRuntime.csproj", StringComparison.Ordinal)),
            "测试工程不得引用 PuddingRuntime —— 一旦引用，本工程的依赖闭包就带上整个宿主层，"
            + "「叶子组件可独立测试」这条性质随之失效。");
    }

    /// <summary>运行时层：组件程序集不得直接引用任何上层程序集。</summary>
    [TestMethod]
    public void ComponentAssembly_DoesNotReferenceUpperLayers()
    {
        var assembly = Assembly.Load(ComponentName);

        var referenced = assembly
            .GetReferencedAssemblies()
            .Select(a => a.Name)
            .Where(n => !string.IsNullOrEmpty(n))
            .Select(n => n!)
            .ToArray();

        // 正对照：抽取层必然引用索引契约所在程序集，否则说明加载到的不是预期产物。
        Assert.Contains(
            AllowedComponentReference,
            referenced,
            $"control: {ComponentName} 应引用 {AllowedComponentReference}，否则这条断言测的不是预期的程序集");

        var violations = FindViolations(referenced);
        Assert.IsEmpty(
            violations,
            $"{ComponentName} 不得引用上层程序集，实际出现：{string.Join(", ", violations)}");
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

    /// <summary>读 csproj 里的 <c>&lt;ProjectReference Include="…" /&gt;</c> 行（原文返回，便于失败时打印）。</summary>
    private static string[] ReadProjectReferences(string csproj) =>
        File.ReadAllLines(csproj)
            .Where(line => line.Contains("<ProjectReference", StringComparison.Ordinal))
            .ToArray();

    /// <summary>用 <see cref="CallerFilePathAttribute"/> 定位同级的指定工程目录（不是测试工程自身）。</summary>
    private static string LocateCsproj(string projectName, [CallerFilePath] string thisFile = "")
    {
        var directory = Path.GetDirectoryName(thisFile)
                        ?? throw new InvalidOperationException("无法从 CallerFilePath 推出目录。");
        var targetDir = Path.GetFullPath(Path.Combine(directory, "..", projectName));
        var csproj = Path.Combine(targetDir, projectName + ".csproj");

        Assert.IsTrue(File.Exists(csproj), $"定位不到 {projectName}.csproj：{csproj}");
        return csproj;
    }
}
