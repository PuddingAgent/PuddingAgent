using PuddingDesktop.Foundation;

namespace PuddingDesktop.FoundationTests;

/// <summary>DS-06: read-only tool/plugin invariants, above all that manifest-only is never executable.</summary>
public sealed class ToolPluginContractTests
{
    private static ToolCatalogEntry Tool(string id, string status, string sourceKind = "BuiltIn", string sourceId = "") =>
        new(id, id, "description", "General", sourceKind, sourceId, status, true, "Medium", 10, []);

    [Fact]
    public void OnlyTheRuntimeAvailableStateCountsAsExecutable()
    {
        Assert.True(ToolPluginText.IsExecutable("Available"));
        Assert.True(ToolPluginText.IsExecutable("available"));
        Assert.False(ToolPluginText.IsExecutable("ManifestOnly"));
        Assert.False(ToolPluginText.IsExecutable("Unavailable"));
        Assert.False(ToolPluginText.IsExecutable(""));
        Assert.False(ToolPluginText.IsExecutable(null), "未知状态必须按不可执行处理");
    }

    [Fact]
    public void DescriptionsNeverClaimAManifestOnlyToolIsUsable()
    {
        Assert.Equal("可执行", ToolPluginText.DescribeRuntimeStatus("Available"));
        Assert.Contains("不可执行", ToolPluginText.DescribeRuntimeStatus("ManifestOnly"), StringComparison.Ordinal);
        Assert.Contains("未知", ToolPluginText.DescribeRuntimeStatus(null), StringComparison.Ordinal);
        Assert.Contains("不可执行", ToolPluginText.DescribeRuntimeStatus("SomethingNew"), StringComparison.Ordinal);
        Assert.Contains("清单无效", ToolPluginText.DescribePluginStatus("ManifestInvalid"), StringComparison.Ordinal);
        Assert.Contains("仅清单声明", ToolPluginText.DescribePluginStatus("ManifestOnly"), StringComparison.Ordinal);
    }

    [Fact]
    public void CatalogEntryDerivesExecutabilityAndPluginOrigin()
    {
        Assert.True(Tool("a", "Available").IsExecutable);
        Assert.False(Tool("a", "ManifestOnly").IsExecutable);
        Assert.False(Tool("a", "Available").IsFromPlugin);
        Assert.True(Tool("a", "ManifestOnly", "Plugin", "pudding.code-search").IsFromPlugin);
        Assert.Equal("无参数", Tool("a", "Available").ParameterSummary);
    }

    [Fact]
    public void ParameterSummaryMarksRequiredParameters()
    {
        var tool = Tool("a", "Available") with
        {
            Parameters =
            [
                new ToolParameterSummary("path", "string", "文件路径", true),
                new ToolParameterSummary("depth", "integer", "深度", false)
            ]
        };
        Assert.Equal("path*、depth", tool.ParameterSummary);
    }

    [Fact]
    public void SearchCoversTheFieldsAnAdministratorWouldUse()
    {
        var tool = Tool("plugin_code_search", "ManifestOnly", "Plugin", "pudding.code-search") with
        {
            Name = "Code Search", Description = "在仓库内检索", Category = "Code"
        };
        Assert.True(ToolPluginText.Matches(tool, null));
        Assert.True(ToolPluginText.Matches(tool, "   "));
        Assert.True(ToolPluginText.Matches(tool, "code_search"));
        Assert.True(ToolPluginText.Matches(tool, "检索"));
        Assert.True(ToolPluginText.Matches(tool, "PUDDING.CODE-SEARCH"));
        Assert.False(ToolPluginText.Matches(tool, "nothing-matches-this"));
    }

    [Fact]
    public void ReportCountsManifestOnlyToolsAndInvalidManifests()
    {
        var report = new PluginCatalogReport(
            [
                new PluginPackageSummary("a", "A", "1.0.0", "ManifestOnly", "", "a/plugin.json", 2),
                new PluginPackageSummary("b", "B", "0.1.0", "ManifestInvalid", "id is missing", "b/plugin.json", 0),
                new PluginPackageSummary("c", "C", "2.0.0", "Discovered", "", "c/plugin.json", 0)
            ],
            [
                new PluginToolDeclaration("a", "t1", "T1", "ManifestOnly", true),
                new PluginToolDeclaration("a", "t2", "T2", "Available", true)
            ],
            []);
        Assert.Equal(1, report.ManifestOnlyToolCount);
        Assert.Equal(1, report.InvalidManifestCount);
        Assert.True(report.Packages[0].IsManifestOnly);
        Assert.True(report.Packages[1].IsInvalid);
        Assert.False(report.Packages[2].IsManifestOnly);
        Assert.False(report.Packages[2].IsInvalid);
    }
}
