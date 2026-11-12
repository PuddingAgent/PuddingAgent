using PuddingCode.Tools;
using PuddingPlatform.Services.Tools;
using PuddingTools.Contracts;
using CoreDescriptor = PuddingCode.Tools.ToolDescriptor;

namespace PuddingPlatformTests.Services;

/// <summary>
/// PA-05B catalog adapter: search/schema/state are backed by the existing tool authority, hidden tools never leak, and an
/// unavailable tool keeps an explicit state instead of being reported as available. Query only — no execution entry.
/// </summary>
[TestClass]
public sealed class PuddingToolCatalogAdapterTests
{
    [TestMethod]
    public async Task Search_FiltersAndPaginatesAvailableTools()
    {
        var adapter = new PuddingToolCatalogAdapter(new FakeCatalog(
            Tool("read_file", "Read file", ToolCategory.FileSystem, ToolSafetyFlags.ReadOnly),
            Tool("write_file", "Write file", ToolCategory.FileSystem, ToolSafetyFlags.RequiresFileWrite),
            Tool("shell", "Run shell", ToolCategory.Shell, ToolSafetyFlags.RequiresShell),
            Tool("hidden", "Hidden", ToolCategory.General, ToolSafetyFlags.None, status: "Hidden")));
        var read = await adapter.SearchAsync(new ToolQuery("file", null, null, null, 10));
        CollectionAssert.AreEqual(new[] { "read_file", "write_file" }, read.Tools.Select(t => t.ToolId).ToArray());
        Assert.IsTrue(read.Complete);
        Assert.AreEqual(ToolSideEffect.Read, read.Tools[0].SideEffect);
        var paged = await adapter.SearchAsync(new ToolQuery(null, null, null, null, 1));
        Assert.AreEqual("read_file", paged.Tools.Single().ToolId);
        Assert.IsFalse(paged.Complete);
        Assert.AreEqual("read_file", paged.Cursor);
        var process = await adapter.SearchAsync(new ToolQuery(null, null, ToolSideEffect.Process, null, 10));
        CollectionAssert.AreEqual(new[] { "shell" }, process.Tools.Select(t => t.ToolId).ToArray());
    }

    [TestMethod]
    public async Task HiddenTools_DoNotLeakSchemaOrState()
    {
        var adapter = new PuddingToolCatalogAdapter(new FakeCatalog(
            Tool("hidden", "Hidden", ToolCategory.General, ToolSafetyFlags.None, status: "Hidden")));
        Assert.IsNull(await adapter.GetSchemaAsync("hidden"));
        Assert.IsNull(await adapter.GetStateAsync("hidden"));
        Assert.AreEqual(0, (await adapter.SearchAsync(new ToolQuery(null, null, null, null, 10))).Tools.Length);
    }

    [TestMethod]
    public async Task SchemaAndUnavailableState_AreExplicit()
    {
        var adapter = new PuddingToolCatalogAdapter(new FakeCatalog(
            Tool("mcp_remote", "Remote", ToolCategory.Network, ToolSafetyFlags.RequiresNetwork, status: "Unavailable")));
        var state = await adapter.GetStateAsync("mcp_remote");
        Assert.AreEqual("Unavailable", state!.Availability);
        Assert.AreEqual("tool.unavailable", state.MissingReason);
        var schema = await adapter.GetSchemaAsync("mcp_remote");
        Assert.AreEqual("mcp_remote", schema!.ToolId);
        Assert.AreEqual("Remote", schema.Help);
    }

    private static CoreDescriptor Tool(string id, string description, ToolCategory category, ToolSafetyFlags safety,
        string status = "Available") => new()
    {
        ToolId = id, Name = id, Description = description, Category = category, Safety = safety,
        RuntimeStatus = status,
    };

    private sealed class FakeCatalog(params CoreDescriptor[] tools) : IPuddingToolCatalogService
    {
        public IReadOnlyList<CoreDescriptor> ListTools(bool enabledByDefaultOnly = false) => tools;
    }
}
