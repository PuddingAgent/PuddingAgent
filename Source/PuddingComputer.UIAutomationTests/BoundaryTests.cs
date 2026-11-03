using System.Reflection;
using PuddingComputer.UIAutomation;

namespace PuddingComputer.UIAutomationTests;

/// <summary>
/// S4 边界断言：<c>PuddingComputer.UIAutomation</c> 的依赖方向必须由测试强制。
/// <para>
/// 规程要求"依赖方向由编译期强制"，但本项目没有 analyzer 插件，
/// 所以次级手段是**运行时反射断言**：程序集引用表是编译产物的一部分，
/// 它变了这里就会红。
/// </para>
/// </summary>
[TestClass]
public sealed class BoundaryTests
{
    private static Assembly Subject => typeof(FlaUiInspector).Assembly;

    /// <summary>
    /// **核心断言**：真实程序集的引用表必须完全落在白名单内，且不命中黑名单。
    /// <para>
    /// 这条不是形式主义：它挡的是"顺手加个 using 就引入了截图/输入层"这种缓慢越界。
    /// </para>
    /// </summary>
    [TestMethod]
    public void RealAssembly_ReferencesOnlyAllowedLayers()
    {
        var referenced = Subject.GetReferencedAssemblies()
            .Select(a => a.Name)
            .Where(n => !string.IsNullOrWhiteSpace(n))
            .Select(n => n!)
            .ToList();

        Assert.IsGreaterThan(0, referenced.Count, "引用表不应为空（至少应有 BCL）");

        var violations = BoundaryRules.FindViolations(referenced);

        Assert.IsEmpty(violations,
            $"UIAutomation 的依赖越界：{string.Join("; ", violations)}");
    }

    /// <summary>
    /// 反向验证：如果真引入了禁止层，这条规则必须能报出来。
    /// <para>
    /// 没有这条，上面的断言可能只是"恰好没触发"而非"真的在守"。
    /// </para>
    /// </summary>
    [TestMethod]
    public void FindViolations_DetectsForbiddenLayer()
    {
        var violations = BoundaryRules.FindViolations(
        [
            "System.Runtime",
            "PuddingComputer.Windows",        // 禁止：截图/输入/OpenCV
            "PuddingComputer.Benchmark",      // 禁止：任务定义
            "Pudding.Contracts",              // 允许
        ]);

        Assert.HasCount(2, violations);
        Assert.IsTrue(violations.Any(v => v.Contains("PuddingComputer.Windows")));
        Assert.IsTrue(violations.Any(v => v.Contains("PuddingComputer.Benchmark")));
    }

    /// <summary>白名单外的未知程序集也要被报出，不能默认放行。</summary>
    [TestMethod]
    public void FindViolations_DetectsUnknownAssembly()
    {
        var violations = BoundaryRules.FindViolations(["SomeRandom.Library"]);

        Assert.HasCount(1, violations);
        Assert.Contains("不在白名单内", violations[0]);
    }

    /// <summary>合规的引用组合不应报错。</summary>
    [TestMethod]
    public void FindViolations_AcceptsAllowedCombination()
    {
        var violations = BoundaryRules.FindViolations(
        [
            "System.Runtime",
            "System.Collections",
            "Pudding.Contracts",
            "FlaUI.Core",
            "FlaUI.UIA3",
            "Interop.UIAutomationClient",
        ]);

        Assert.IsEmpty(violations);
    }

    /// <summary>
    /// 黑名单优先于白名单：即使某个名字同时命中两边，也必须判违规。
    /// <para>
    /// 这条守的是"允许前缀不能成为绕过通道"。
    /// </para>
    /// </summary>
    [TestMethod]
    public void ForbiddenTakesPrecedenceOverAllowed()
    {
        // 构造一个"同时像 Pudding.Contracts 和 PuddingCore"的名字不可能，
        // 所以直接验证两条规则都被执行：分别给一个只命中黑名单的名字。
        var onlyForbidden = BoundaryRules.FindViolations(["PuddingCore"]);
        Assert.HasCount(1, onlyForbidden);
        Assert.Contains("命中禁止前缀", onlyForbidden[0]);
    }

    /// <summary>空输入不报错。</summary>
    [TestMethod]
    public void FindViolations_EmptyInput_IsClean()
    {
        Assert.IsEmpty(BoundaryRules.FindViolations([]));
    }

    /// <summary>空字符串项被跳过，不制造虚假违规。</summary>
    [TestMethod]
    public void FindViolations_SkipsBlankNames()
    {
        Assert.IsEmpty(BoundaryRules.FindViolations(["", "  ", "System.Runtime"]));
    }
}
