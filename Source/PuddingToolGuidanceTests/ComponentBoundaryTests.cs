using System.Runtime.CompilerServices;

namespace PuddingToolGuidanceTests;

/// <summary>
/// S4 边界断言：引导组件必须是**零依赖叶子**。
/// <para>
/// 它会随每一次工具调用执行 —— 一旦悄悄长出一条对宿主／DI／工具注册表的依赖，
/// 成本会落在每个调用上，而且很难在生产被发现。
/// </para>
/// </summary>
[TestClass]
public sealed class ComponentBoundaryTests
{
    [TestMethod]
    public void Component_csproj_declares_no_project_reference()
    {
        var csproj = LocateComponentCsproj();
        var declarations = File
            .ReadAllLines(csproj)
            .Where(l => l.Contains("<ProjectReference", StringComparison.Ordinal))
            .ToArray();

        Assert.IsEmpty(
            declarations,
            "PuddingToolGuidance 必须零工程依赖（叶子组件）；实际声明：\n" + string.Join("\n", declarations));
    }

    [TestMethod]
    public void Component_assembly_references_only_bcl()
    {
        var assembly = typeof(PuddingToolGuidance.GuidanceEngine).Assembly;
        var referenced = assembly.GetReferencedAssemblies()
            .Select(a => a.Name)
            .Where(n => !string.IsNullOrEmpty(n))
            .Select(n => n!)
            .ToArray();

        // 正对照：必须真的读到引用，否则「没有 Pudding 引用」是空转。
        Assert.Contains("System.Runtime", referenced, "control: 应能读到 System.Runtime");

        var puddingRefs = referenced
            .Where(n => n.StartsWith("Pudding", StringComparison.Ordinal))
            .ToArray();

        Assert.IsEmpty(puddingRefs, "不得引用任何 Pudding* 程序集，实际：" + string.Join(", ", puddingRefs));
    }

    [TestMethod]
    public void Test_project_references_only_the_component()
    {
        var csproj = LocateCsproj("PuddingToolGuidanceTests");
        var declarations = File.ReadAllLines(csproj)
            .Where(l => l.Contains("<ProjectReference", StringComparison.Ordinal))
            .ToArray();

        Assert.HasCount(1, declarations, "测试工程只应引用被测组件");
        Assert.IsTrue(
            declarations[0].Contains("PuddingToolGuidance.csproj", StringComparison.Ordinal),
            "测试工程必须引用 PuddingToolGuidance 自身");
    }

    private static string LocateCsproj(string name, [CallerFilePath] string thisFile = "")
    {
        var dir = Path.GetDirectoryName(thisFile) ?? throw new InvalidOperationException("no dir");
        var target = Path.GetFullPath(Path.Combine(dir, "..", name, name + ".csproj"));
        Assert.IsTrue(File.Exists(target), $"定位不到 {name}.csproj：{target}");
        return target;
    }

    private static string LocateComponentCsproj([CallerFilePath] string thisFile = "") =>
        LocateCsproj("PuddingToolGuidance", thisFile);
}
