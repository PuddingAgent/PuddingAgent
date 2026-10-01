using Pudding.Contracts.Desktop;
using Pudding.DesktopService;

namespace DesktopServiceTests;

/// <summary>目标注册表默认实现：未登记一律不可信、不报告陈旧的活跃页、注销即失效。</summary>
public sealed class BrowserTargetRegistryTests
{
    [Fact]
    public void UnregisteredContextsAreUntrustedAndUnknownPagesAreNotAgentTargets()
    {
        var registry = new BrowserTargetRegistry();

        // fail closed：没登记 ≠ 可信。
        Assert.Equal(DesktopContextTrust.Untrusted, registry.TrustFor("ctx-unknown"));
        Assert.False(registry.IsAgentTarget("ctx-unknown", "p-1"));
        Assert.Null(registry.ActivePage);

        registry.RegisterContext("ctx-1", DesktopContextTrust.AgentAuthorized);
        registry.RegisterPage("ctx-1", "p-1", isAgentTarget: true);

        Assert.Equal(DesktopContextTrust.AgentAuthorized, registry.TrustFor("ctx-1"));
        Assert.True(registry.IsAgentTarget("ctx-1", "p-1"));
        Assert.False(registry.IsAgentTarget("ctx-1", "p-2"));   // 同上下文但未登记为目标
        Assert.False(registry.IsAgentTarget("ctx-2", "p-1"));   // 未登记的上下文
    }

    [Fact]
    public void ActivePageIsReportedAndCanBeCleared()
    {
        var registry = new BrowserTargetRegistry();
        registry.RegisterContext("ctx-1", DesktopContextTrust.Workbench);
        registry.RegisterPage("ctx-1", "p-1", isActive: true);
        registry.RegisterPage("ctx-1", "p-2");

        Assert.Equal(("ctx-1", "p-1"), registry.ActivePage);

        registry.SetActivePage("ctx-1", "p-2");
        Assert.Equal(("ctx-1", "p-2"), registry.ActivePage);

        registry.SetActivePage(null, null);
        Assert.Null(registry.ActivePage);
    }

    [Fact]
    public void UnregisteringTheActivePageNeverLeavesAStaleActivePage()
    {
        var registry = new BrowserTargetRegistry();
        registry.RegisterContext("ctx-1", DesktopContextTrust.AgentAuthorized);
        registry.RegisterPage("ctx-1", "p-1", isAgentTarget: true, isActive: true);

        registry.UnregisterPage("ctx-1", "p-1");

        // 已不存在的页面不得继续被当作"当前页"。
        Assert.Null(registry.ActivePage);
        Assert.False(registry.IsAgentTarget("ctx-1", "p-1"));

        registry.RegisterPage("ctx-1", "p-2", isActive: true);
        registry.UnregisterContext("ctx-1");

        Assert.Null(registry.ActivePage);
        Assert.Equal(DesktopContextTrust.Untrusted, registry.TrustFor("ctx-1"));
    }

    [Fact]
    public void ClearResetsEverything()
    {
        var registry = new BrowserTargetRegistry();
        registry.RegisterContext("ctx-1", DesktopContextTrust.Workbench);
        registry.RegisterPage("ctx-1", "p-1", isAgentTarget: true, isActive: true);

        registry.Clear();

        Assert.Equal(DesktopContextTrust.Untrusted, registry.TrustFor("ctx-1"));
        Assert.False(registry.IsAgentTarget("ctx-1", "p-1"));
        Assert.Null(registry.ActivePage);
    }
}