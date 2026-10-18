using System.Reflection;
using PuddingBrowser.Authorization;

namespace PuddingBrowser.AuthorizationTests;

/// <summary>
/// 组件边界断言（规程 S4）：**运行时**验证依赖方向，而不是靠注释约定。
/// 破坏方式：给组件或本测试工程加一条指向宿主的引用 ⇒ 本测试立刻变红。
/// </summary>
[TestClass]
public sealed class ComponentBoundaryTests
{
    private static readonly string[] Forbidden =
    [
        "PuddingHost",
        "PuddingRuntime",
        "PuddingAgent",
        "Pudding.DesktopService",
        "Pudding.DesktopSurface.Browser",
        "PuddingDesktop",
        "PuddingBrowser.WebView2",
        "PuddingBrowser.WinUI",
        "PuddingBrowser.Automation",
    ];

    [TestMethod]
    public void TheComponentReferencesContractsOnly()
    {
        var referenced = typeof(AgentTargetSessionAuthority).Assembly
            .GetReferencedAssemblies()
            .Select(name => name.Name ?? string.Empty)
            .ToArray();

        var violations = referenced
            .Where(name => Forbidden.Contains(name, StringComparer.Ordinal))
            .ToArray();

        CollectionAssert.AreEqual(Array.Empty<string>(), violations,
            "PuddingBrowser.Authorization must not reference host/UI/driver assemblies.");
    }

    [TestMethod]
    public void TheTestProjectDoesNotReferenceForbiddenAssemblies()
    {
        var referenced = typeof(ComponentBoundaryTests).Assembly
            .GetReferencedAssemblies()
            .Select(name => name.Name ?? string.Empty)
            .ToArray();

        var violations = referenced
            .Where(name => Forbidden.Contains(name, StringComparer.Ordinal))
            .ToArray();

        // 本测试工程只允许引用被测组件（及其传递依赖 Pudding.Contracts）。
        CollectionAssert.AreEqual(Array.Empty<string>(), violations,
            "PuddingBrowser.AuthorizationTests must reference the component under test only.");
    }

    [TestMethod]
    public void TheStateMachineIsTestableWithoutAnyHost()
    {
        // 不启动宿主、不需要 UI 线程即可构造并驱动状态机 —— 这正是拆出本组件的目的。
        var authority = new AgentTargetSessionAuthority();
        authority.OnBridgeReady(Pudding.Contracts.ConnectionGeneration.Require(1));
        authority.SelectTarget(
            new Pudding.Contracts.Desktop.DesktopPageTarget("ctx", "page"),
            AgentTargetControlHandle.For("component-test-session"));

        Assert.IsTrue(authority.Capture().HasTarget);
    }
}
