using System.Text.Json;
using PuddingCode.Abstractions;
using PuddingCode.Models;
using PuddingCode.Platform;
using PuddingCode.Runtime;
using PuddingCode.Tools;
using PuddingRuntime.Services;
using PuddingRuntime.Services.Tools;

namespace PuddingRuntimeTests.Tools;

/// <summary>
/// 委派 worktree 执行根统一回归测试。
/// 背景：run_20260821_230951_dbff4b8075c1 中防火墙/审批用进程级静态 workspace root
/// 校验，而文件工具用 ToolExecutionContext.WorkingDirectory，同一 worktree 路径
/// 在防火墙被误判越界。修复后 FirewallContext.WorkingDirectory 在委派创建时冻结，
/// 防火墙、审批、文件工具使用同一执行根。
/// </summary>
[TestClass]
public sealed class AgentFirewallWorkingDirectoryTests
{
    private static string CreateTempRoot()
    {
        var path = Path.Combine(
            Path.GetTempPath(),
            "pudding-fw-wd-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static FirewallContext BuildContext(
        string toolId,
        string path,
        string? workingDirectory) => new()
    {
        WorkspaceId = "default",
        SessionId = "session",
        AgentInstanceId = "agent",
        ToolId = toolId,
        ArgumentsJson = JsonSerializer.Serialize(new { path }),
        RuntimeMode = RuntimeExecutionMode.Normal,
        WorkingDirectory = workingDirectory,
    };

    [TestMethod]
    public async Task WorkspaceGate_Allows_Write_Inside_Delegated_Worktree_Root()
    {
        var worktree = CreateTempRoot();
        try
        {
            var firewall = new AgentFirewall();
            var target = Path.Combine(worktree, "src", "NewFile.tsx");

            var decision = await firewall.EvaluateAsync(
                BuildContext("file_write", target, worktree),
                CancellationToken.None);

            Assert.IsTrue(decision.Allowed, decision.DenyReason);
        }
        finally
        {
            Directory.Delete(worktree, recursive: true);
        }
    }

    [TestMethod]
    public async Task WorkspaceGate_Denies_Write_Outside_Delegated_Worktree_Root()
    {
        var worktree = CreateTempRoot();
        var outside = CreateTempRoot();
        try
        {
            var firewall = new AgentFirewall();

            var decision = await firewall.EvaluateAsync(
                BuildContext("file_write", Path.Combine(outside, "escape.txt"), worktree),
                CancellationToken.None);

            Assert.IsFalse(decision.Allowed);
            Assert.AreEqual(FirewallGate.Workspace, decision.DeniedAtGate);
        }
        finally
        {
            Directory.Delete(worktree, recursive: true);
            Directory.Delete(outside, recursive: true);
        }
    }

    [TestMethod]
    public async Task ToolApproval_BuiltinPolicy_Resolves_FileWrite_Inside_WorkingDirectory()
    {
        var worktree = CreateTempRoot();
        try
        {
            var approval = new InMemoryToolApprovalService();
            var descriptor = new SampleWriteTool().Descriptor with { ToolId = "file_write" };

            var check = await approval.CheckAsync(
                new ToolApprovalExecutionRequest
                {
                    WorkspaceId = "default",
                    SessionId = "session",
                    AgentInstanceId = "agent",
                    UserId = "admin",
                    ToolId = "file_write",
                    ActualArgumentsJson = JsonSerializer.Serialize(
                        new { path = Path.Combine(worktree, "src", "NewFile.tsx") }),
                    WorkingDirectory = worktree,
                },
                descriptor);

            Assert.IsTrue(check.IsApproved, check.Message);
            Assert.AreEqual("builtin_workspace_file_write", check.AllowlistRuleId);
        }
        finally
        {
            Directory.Delete(worktree, recursive: true);
        }
    }

    [TestMethod]
    public async Task ToolApproval_BuiltinPolicy_Rejects_FileWrite_Outside_WorkingDirectory()
    {
        var worktree = CreateTempRoot();
        var outside = CreateTempRoot();
        try
        {
            var approval = new InMemoryToolApprovalService();
            var descriptor = new SampleWriteTool().Descriptor with { ToolId = "file_write" };

            var check = await approval.CheckAsync(
                new ToolApprovalExecutionRequest
                {
                    WorkspaceId = "default",
                    SessionId = "session",
                    AgentInstanceId = "agent",
                    UserId = "admin",
                    ToolId = "file_write",
                    ActualArgumentsJson = JsonSerializer.Serialize(
                        new { path = Path.Combine(outside, "escape.txt") }),
                    WorkingDirectory = worktree,
                },
                descriptor);

            // 隐式审计层可以有自己的判定；但执行根之外的目标绝不能命中
            // workspace-scope 内置策略——那等于审批与文件工具使用了不同的根。
            Assert.AreNotEqual("builtin_workspace_file_write", check.AllowlistRuleId);
        }
        finally
        {
            Directory.Delete(worktree, recursive: true);
            Directory.Delete(outside, recursive: true);
        }
    }

    [Tool(
        id: "sample_write",
        name: "Sample write",
        description: "Non read-only sample tool for approval tests.",
        permission: ToolPermissionLevel.Medium)]
    private sealed class SampleWriteTool : PuddingToolBase<object>
    {
        protected override Task<ToolExecutionResult> ExecuteCoreAsync(
            object args,
            ToolExecutionContext context,
            CancellationToken ct)
            => Task.FromResult(ToolExecutionResult.Ok("executed"));
    }
}
