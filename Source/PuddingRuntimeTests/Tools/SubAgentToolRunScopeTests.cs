using PuddingCode.Configuration;
using PuddingCode.Tools;
using PuddingRuntime.Services.Skills;

namespace PuddingRuntimeTests.Tools;

/// <summary>
/// 2026-11-25：pool_action=logs 的**读取范围**解析。
/// <para>
/// 这一层守的是「读范围必须与写范围一致」。归属校验在状态查不到时是放行的，
/// 如果读取端仍按 WorkspacesRoot 全盘扫，那条放行就会把别的 agent 的运行内容读出来。
/// 因此这里必须与写入端（<see cref="PuddingDataPaths.SubAgentRunRoot"/>）派生到同一个目录。
/// </para>
/// </summary>
[TestClass]
public sealed class SubAgentToolRunScopeTests
{
    [TestMethod]
    public void ResolveOwnRunScope_PointsAtSameDirectoryAsRunWriter()
    {
        var paths = PuddingDataPaths.FromRoot(@"C:\data-root");

        var scope = SubAgentTool.ResolveOwnRunScope(paths, Context(workspaceId: "ws1", agentId: "agent.1"));

        Assert.AreEqual(
            @"C:\data-root\workspaces\ws1\agents\agent.1\runs",
            scope,
            "范围必须落在该 agent 的 runs 根上");

        // 与写入端同源：归档写在哪，读取就指向哪。
        var writtenRunDir = paths.SubAgentRunRoot("ws1", "agent.1", "run_20260101_000000_x");
        Assert.AreEqual(Path.GetDirectoryName(writtenRunDir), scope);
    }

    [TestMethod]
    public void ResolveOwnRunScope_PrefersConfigurationAgentForDelegatedRuns()
    {
        var paths = PuddingDataPaths.FromRoot(@"C:\data-root");

        // 委托运行的 AgentInstanceId 是临时子会话身份，归档却写在根 Agent 目录下。
        var scope = SubAgentTool.ResolveOwnRunScope(
            paths,
            Context(workspaceId: "ws1", agentId: "ephemeral-sub", configurationAgentId: "agent.1"));

        Assert.AreEqual(
            Path.Combine(paths.WorkspaceAgentRoot("ws1", "agent.1"), "runs"),
            scope,
            "有根 Agent 时必须用它，否则嵌套调用读不到自己写的归档");
    }

    [TestMethod]
    public void ResolveOwnRunScope_ReturnsNull_WhenAgentIdentityMissing()
    {
        var paths = PuddingDataPaths.FromRoot(@"C:\data-root");

        Assert.IsNull(SubAgentTool.ResolveOwnRunScope(paths, Context(workspaceId: "ws1", agentId: "")));
    }

    [TestMethod]
    public void ResolveOwnRunScope_DefaultsWorkspaceToDefault()
    {
        var paths = PuddingDataPaths.FromRoot(@"C:\data-root");

        var scope = SubAgentTool.ResolveOwnRunScope(paths, Context(workspaceId: "", agentId: "agent.1"));

        Assert.AreEqual(Path.Combine(paths.WorkspaceAgentRoot("default", "agent.1"), "runs"), scope);
    }

    private static ToolExecutionContext Context(
        string workspaceId,
        string agentId,
        string? configurationAgentId = null) => new()
    {
        WorkspaceId = workspaceId,
        SessionId = "session-1",
        AgentInstanceId = agentId,
        ConfigurationAgentInstanceId = configurationAgentId,
    };
}
