using PuddingCode.Tools;
using PuddingRuntime.Services.Tools;

namespace PuddingRuntimeTests.Tools;

/// <summary>
/// test_tool 的描述符解析必须同时覆盖内置注册表与 host-service 插件目录。
/// 回归背景（2026-11-13 实测）：file_read / browser_navigate / sleep 都是 host-service 工具，
/// 只查内置注册表会返回 registered=false，而它们在同一时刻完全可调用——
/// 这会把「工具没暴露」误诊成「工具没安装」。
/// </summary>
[TestClass]
public sealed class AgentTestToolResolutionTests
{
    private static ToolDescriptor Descriptor(string id, string? sourceId = null) => new()
    {
        ToolId = id,
        Name = id,
        Description = id,
        SourceKind = sourceId is null ? "BuiltIn" : "Plugin",
        SourceId = sourceId,
    };

    [TestMethod]
    public void IsExposed_ReturnsFalse_WhenThePolicyFiltersTheToolOut()
    {
        // registered=true 不等于可加载：provider_balance 实测 registered=true / is_available=true，
        // 但 ListAvailable 里没有它（能力策略不含），search_tools 因此永远搜不到。
        var available = new[] { Descriptor("plugin_search") };

        Assert.IsFalse(AgentTestTool.IsExposed(available, "provider_balance"));
        Assert.IsTrue(AgentTestTool.IsExposed(available, "plugin_search"));
    }

    [TestMethod]
    public void IsExposed_IsCaseInsensitive()
    {
        var available = new[] { Descriptor("provider_balance") };

        Assert.IsTrue(AgentTestTool.IsExposed(available, "PROVIDER_BALANCE"));
    }

    [TestMethod]
    public void RegistryHit_IsReportedAsRegistry()
    {
        var result = AgentTestTool.ResolveDescriptor(
            Descriptor("plugin_search"), new StubCatalog(), "default", "plugin_search");

        Assert.AreEqual("registry", result.ResolvedBy);
        Assert.IsNotNull(result.Descriptor);
        Assert.AreEqual("plugin_search", result.Descriptor.ToolId);
    }

    [TestMethod]
    public void HostServiceTool_IsResolvedWhenRegistryMisses()
    {
        var catalog = new StubCatalog(Descriptor("file_read", "pudding.files"));

        var result = AgentTestTool.ResolveDescriptor(null, catalog, "default", "file_read");

        Assert.AreEqual("host-catalog", result.ResolvedBy);
        Assert.IsNotNull(result.Descriptor);
        Assert.AreEqual("pudding.files", result.Descriptor.SourceId);
        Assert.AreEqual("Plugin", result.Descriptor.SourceKind);
    }

    [TestMethod]
    public void HostServiceTool_MatchingIsCaseInsensitive()
    {
        var catalog = new StubCatalog(Descriptor("browser_navigate", "pudding.browser"));

        var result = AgentTestTool.ResolveDescriptor(null, catalog, "default", "BROWSER_NAVIGATE");

        Assert.AreEqual("host-catalog", result.ResolvedBy);
    }

    [TestMethod]
    public void UnknownTool_ReportsNoneAndSuggestsNearMatches()
    {
        var catalog = new StubCatalog(
            Descriptor("file_read"), Descriptor("file_write"), Descriptor("sleep"));

        var result = AgentTestTool.ResolveDescriptor(null, catalog, "default", "file");

        Assert.IsNull(result.Descriptor);
        Assert.AreEqual("none", result.ResolvedBy);
        Assert.AreEqual("file_read, file_write", result.Suggestions);
    }

    [TestMethod]
    public void MissingCatalog_DoesNotThrowAndReportsNone()
    {
        var result = AgentTestTool.ResolveDescriptor(null, null, "default", "anything");

        Assert.IsNull(result.Descriptor);
        Assert.AreEqual("none", result.ResolvedBy);
        Assert.IsNull(result.Suggestions);
    }

    private sealed class StubCatalog(params ToolDescriptor[] tools) : IPuddingToolCatalogService
    {
        public IReadOnlyList<ToolDescriptor> ListTools(bool enabledByDefaultOnly = false) => tools;
    }
}
