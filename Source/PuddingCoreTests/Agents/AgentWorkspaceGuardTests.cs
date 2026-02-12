using PuddingCode.Agents;
using PuddingCode.Configuration;
using PuddingCode.SubAgents;

namespace PuddingCoreTests.Agents;

[TestClass]
public sealed class AgentWorkspaceGuardTests
{
    // 辅助方法：创建带有默认权限和测试 template 目录的 guard
    private static (AgentWorkspaceGuard Guard, string WorkspaceRoot) CreateGuard()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "pudding-guard-tests", Guid.NewGuid().ToString("N"));
        var dataRoot = Path.Combine(tempRoot, "data");
        Directory.CreateDirectory(dataRoot);

        // 创建 agent-templates 根目录（AgentProfileProvider 需要）
        var agentTemplatesRoot = Path.Combine(dataRoot, "agent-templates");
        Directory.CreateDirectory(agentTemplatesRoot);

        var paths = PuddingDataPaths.FromRoot(dataRoot);
        var profileProvider = new AgentProfileProvider(paths);
        var guard = new AgentWorkspaceGuard(profileProvider);

        // workspace 根目录（模拟）
        var workspaceRoot = Path.Combine(dataRoot, "workspaces", "default");
        Directory.CreateDirectory(workspaceRoot);

        return (guard, workspaceRoot);
    }

    [TestMethod]
    public void CanWrite_PathUnderWorkspace_Allowed()
    {
        var (guard, workspaceRoot) = CreateGuard();
        var filePath = Path.Combine(workspaceRoot, "test.txt");

        var decision = guard.CanWrite("test-agent", workspaceRoot, filePath);

        Assert.IsTrue(decision.Allowed);
    }

    [TestMethod]
    public void CanWrite_PathInDataConfig_Denied()
    {
        var (guard, workspaceRoot) = CreateGuard();
        // 创建一个 data/config 目录并指向其中的文件（位于 workspace 外部）
        var configDir = Path.Combine(Path.GetDirectoryName(workspaceRoot)!, "..", "data", "config");
        var configDirFull = Path.GetFullPath(configDir);
        Directory.CreateDirectory(configDirFull);
        var configFile = Path.Combine(configDirFull, "system.json");

        var decision = guard.CanWrite("test-agent", workspaceRoot, configFile);

        Assert.IsFalse(decision.Allowed);
        Assert.IsNotNull(decision.MatchedRule);
        // 路径在 workspace 外部，相对路径以 ../ 开头，匹配 ../** deny 规则
        StringAssert.Contains(decision.MatchedRule, "../");
    }

    [TestMethod]
    public void CanWrite_PathTraversal_Denied()
    {
        var (guard, workspaceRoot) = CreateGuard();
        var traversalPath = Path.Combine(workspaceRoot, "..", "secret.txt");
        // 使用 Path.GetFullPath 规范化后传给 guard 测试
        var fullPath = Path.GetFullPath(traversalPath);

        var decision = guard.CanWrite("test-agent", workspaceRoot, fullPath);

        Assert.IsFalse(decision.Allowed);
        StringAssert.Contains(decision.Reason?.ToLower(), "traversal");
    }

    [TestMethod]
    public void CanRead_PathUnderWorkspace_Allowed()
    {
        var (guard, workspaceRoot) = CreateGuard();
        var filePath = Path.Combine(workspaceRoot, "readme.md");

        var decision = guard.CanRead("test-agent", workspaceRoot, filePath);

        Assert.IsTrue(decision.Allowed);
    }

    [TestMethod]
    public void CanRead_PathOutsideWorkspace_Denied()
    {
        var (guard, workspaceRoot) = CreateGuard();
        var outsidePath = Path.Combine(Path.GetTempPath(), "outside.txt");

        var decision = guard.CanRead("test-agent", workspaceRoot, outsidePath);

        Assert.IsFalse(decision.Allowed);
    }

    [TestMethod]
    public void CanExecuteTool_MemorySearch_Allowed()
    {
        var (guard, _) = CreateGuard();
        // 默认 Allow 列表为空时允许所有工具
        var decision = guard.CanExecuteTool("test-agent", "search_memory");

        Assert.IsTrue(decision.Allowed);
    }

    [TestMethod]
    public void CanExecuteTool_Shell_Allowed_When_No_Deny_List()
    {
        // 这个测试需要手动构造包含 deny 的权限
        // 由于 AgentWorkspaceGuard 依赖 AgentProfileProvider 加载权限，
        // 这里测试默认行为（空 deny 列表时允许所有工具）
        // deny 逻辑在 MatchGlob 单元测试中覆盖
        var (guard, _) = CreateGuard();

        // 默认行为：没有 deny，没有 allow → 允许所有（fallback）
        var decision = guard.CanExecuteTool("test-agent", "shell");

        Assert.IsTrue(decision.Allowed);
    }

    // ── MatchGlob 单元测试 ──

    [TestMethod]
    public void MatchGlob_ExactMatch_ReturnsTrue()
    {
        Assert.IsTrue(AgentWorkspaceGuard.MatchGlob("workspace/test.txt", "workspace/test.txt"));
    }

    [TestMethod]
    public void MatchGlob_SingleStar_MatchesSingleLevel()
    {
        Assert.IsTrue(AgentWorkspaceGuard.MatchGlob("workspace/test.txt", "workspace/*"));
        Assert.IsTrue(AgentWorkspaceGuard.MatchGlob("workspace/test.txt", "workspace/*.txt"));
    }

    [TestMethod]
    public void MatchGlob_DoubleStar_MatchesMultipleLevels()
    {
        Assert.IsTrue(AgentWorkspaceGuard.MatchGlob("workspace/sub/dir/file.txt", "workspace/**"));
        Assert.IsTrue(AgentWorkspaceGuard.MatchGlob("workspace/sub/dir/file.txt", "**"));
        Assert.IsTrue(AgentWorkspaceGuard.MatchGlob("workspace/a/b/c/file.txt", "workspace/**/*.txt"));
    }

    [TestMethod]
    public void MatchGlob_DenyPattern_MatchesNestedPath()
    {
        Assert.IsTrue(AgentWorkspaceGuard.MatchGlob("data/config/system.json", "data/config/**"));
        Assert.IsTrue(AgentWorkspaceGuard.MatchGlob("data/databases/pudding.db", "data/databases/**"));
    }

    [TestMethod]
    public void MatchGlob_PathTraversal_Matches()
    {
        Assert.IsTrue(AgentWorkspaceGuard.MatchGlob("../secret.txt", "../**"));
    }

    [TestMethod]
    public void MatchGlob_NoMatch_ReturnsFalse()
    {
        Assert.IsFalse(AgentWorkspaceGuard.MatchGlob("workspace/test.txt", "data/**"));
        Assert.IsFalse(AgentWorkspaceGuard.MatchGlob("workspace/test.txt", "workspace/*.json"));
    }

    // ── 清理 ──

    [TestCleanup]
    public void Cleanup()
    {
        // 测试期间创建了临时目录，无法在 TestCleanup 中集中清理（状态分散在方法中）。
        // 使用 TestInitialize 统一管理临时目录。
    }
}
