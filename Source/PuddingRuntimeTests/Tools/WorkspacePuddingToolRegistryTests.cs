using PuddingCode.Tools;
using PuddingRuntime.Services.Tools;

namespace PuddingRuntimeTests.Tools;

[TestClass]
public sealed class WorkspacePuddingToolRegistryTests
{
    [TestMethod]
    public void Workspace_Source_Is_Visible_Only_Inside_Its_Workspace()
    {
        var alphaTool = new StubTool("mcp__alpha__echo");
        var source = new StubWorkspaceSource(new Dictionary<string, IReadOnlyList<IPuddingTool>>
        {
            ["workspace-alpha"] = [alphaTool],
        });
        var registry = new PuddingToolRegistry(
            [],
            workspaceToolSources: [source]);

        Assert.AreSame(alphaTool, registry.GetTool(alphaTool.Descriptor.ToolId, "workspace-alpha"));
        Assert.IsNull(registry.GetTool(alphaTool.Descriptor.ToolId, "workspace-beta"));
        Assert.IsNull(registry.GetTool(alphaTool.Descriptor.ToolId));
        Assert.AreEqual(1, registry.ListDescriptors("workspace-alpha").Count);
        Assert.AreEqual(0, registry.ListDescriptors("workspace-beta").Count);
        Assert.AreEqual(0, registry.ListDescriptors().Count);
    }

    [TestMethod]
    public void Workspace_Source_Still_Uses_Global_Duplicate_Id_Guard()
    {
        var builtIn = new StubTool("duplicate_tool");
        var source = new StubWorkspaceSource(new Dictionary<string, IReadOnlyList<IPuddingTool>>
        {
            ["workspace-alpha"] = [new StubTool("duplicate_tool")],
        });
        var registry = new PuddingToolRegistry(
            [builtIn],
            workspaceToolSources: [source]);

        var ex = Assert.ThrowsExactly<InvalidOperationException>(
            () => registry.ListDescriptors("workspace-alpha"));
        StringAssert.Contains(ex.Message, "Duplicate tool id 'duplicate_tool'");
    }

    private sealed class StubWorkspaceSource(
        IReadOnlyDictionary<string, IReadOnlyList<IPuddingTool>> toolsByWorkspace)
        : IWorkspacePuddingToolSource
    {
        public string SourceId => "test-workspace-source";

        public IReadOnlyList<IPuddingTool> ListTools(string workspaceId) =>
            toolsByWorkspace.TryGetValue(workspaceId, out var tools) ? tools : [];
    }

    private sealed class StubTool(string toolId) : IPuddingTool
    {
        public ToolDescriptor Descriptor { get; } = new()
        {
            ToolId = toolId,
            Name = toolId,
            Description = "Workspace-scoped test tool.",
        };

        public Task<ToolExecutionResult> ExecuteAsync(
            ToolExecutionRequest request,
            CancellationToken ct = default) =>
            Task.FromResult(ToolExecutionResult.Ok("ok"));
    }
}
