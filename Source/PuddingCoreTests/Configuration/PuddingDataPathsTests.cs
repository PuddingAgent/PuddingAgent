using PuddingCode.Configuration;

namespace PuddingCoreTests.Configuration;

[TestClass]
public sealed class PuddingDataPathsTests
{
    [TestMethod]
    public void FromRoot_Normalizes_All_Expected_Directories()
    {
        var root = Path.Combine(Path.GetTempPath(), "pudding-data-root");

        var paths = PuddingDataPaths.FromRoot(root);

        Assert.AreEqual(Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar), paths.DataRoot);
        Assert.AreEqual(Path.Combine(paths.DataRoot, "config"), paths.ConfigRoot);
        Assert.AreEqual(Path.Combine(paths.DataRoot, "agent-templates"), paths.AgentTemplatesRoot);
        Assert.AreEqual(Path.Combine(paths.DataRoot, "agents"), paths.AgentInstancesRoot);
        Assert.AreEqual(Path.Combine(paths.DataRoot, "workspaces"), paths.WorkspacesRoot);
        Assert.AreEqual(Path.Combine(paths.DataRoot, "logs", "sessions"), paths.SessionLogsRoot);
        Assert.AreEqual(Path.Combine(paths.DataRoot, "runtime", "traces"), paths.RuntimeTracesRoot);
        Assert.AreEqual(Path.Combine(paths.DataRoot, "runtime", "event-queue"), paths.EventQueueRoot);
        Assert.AreEqual(Path.Combine(paths.DataRoot, "databases"), paths.DatabasesRoot);
        Assert.AreEqual(Path.Combine(paths.DataRoot, "tmp"), paths.TempRoot);
    }

    [TestMethod]
    public void AgentInstanceConfigFile_Uses_Instance_Id()
    {
        var paths = PuddingDataPaths.FromRoot(Path.Combine(Path.GetTempPath(), "pudding-data-root"));

        var file = paths.AgentInstanceConfigFile("default.general-assistant-001", "llm.json");

        Assert.AreEqual(
            Path.Combine(paths.DataRoot, "agents", "default.general-assistant-001", "config", "llm.json"),
            file);
    }

    [TestMethod]
    public void WorkspaceAgentRefFile_Uses_Workspace_And_Instance_Id()
    {
        var paths = PuddingDataPaths.FromRoot(Path.Combine(Path.GetTempPath(), "pudding-data-root"));

        var file = paths.WorkspaceAgentRefFile("default", "default.general-assistant-001");

        Assert.AreEqual(
            Path.Combine(paths.DataRoot, "workspaces", "default", "agents", "default.general-assistant-001", "ref.json"),
            file);
    }
}
